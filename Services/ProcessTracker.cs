using DesktopTie.Config;
using DesktopTie.Desktop;
using DesktopTie.Models;
using DesktopTie.Tray;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Management;
using System.Runtime.Versioning;

namespace DesktopTie.Services;

[SupportedOSPlatform("windows")]
public sealed class ProcessTracker : IHostedService, IDisposable, IProcessTracker
{
    private static readonly TimeSpan CleanupSweepInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan DesktopSampleInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan ProcessPollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan DesktopSampleSelectionBias = TimeSpan.FromMilliseconds(75);
    private static readonly TimeSpan MaxPriorDesktopSampleAge = TimeSpan.FromSeconds(2);
    private const int MaxDesktopSamples = 256;

    private readonly ILogger<ProcessTracker> _logger;
    private readonly IVirtualDesktopManager _virtualDesktopManager;
    private readonly IAgentRuntimeState _runtimeState;
    private readonly IAgentEventLog _eventLog;
    private readonly HashSet<string> _ignoredProcesses;
    private readonly TimeSpan _trackingTimeout;
    private readonly ConcurrentDictionary<int, LaunchRecord> _launchRecords = new();
    private readonly ConcurrentDictionary<int, ConcurrentDictionary<IntPtr, byte>> _processedWindowsByProcess = new();
    private readonly ConcurrentDictionary<int, byte> _knownProcessIds = new();
    private readonly object _desktopSamplesLock = new();
    private readonly Queue<DesktopSample> _desktopSamples = new();
    private ManagementEventWatcher? _watcher;
    private PeriodicTimer? _expirationTimer;
    private PeriodicTimer? _desktopSampleTimer;
    private PeriodicTimer? _processPollTimer;
    private CancellationTokenSource? _expirationCts;
    private Task? _expirationTask;
    private Task? _desktopSampleTask;
    private Task? _processPollTask;
    private bool _serviceStarted;
    private bool _watcherStarted;

    public ProcessTracker(
        ILogger<ProcessTracker> logger,
        IVirtualDesktopManager virtualDesktopManager,
        IAgentRuntimeState runtimeState,
        IAgentEventLog eventLog,
        IOptions<AgentSettings> agentSettings)
    {
        var settings = agentSettings.Value;
        _logger = logger;
        _virtualDesktopManager = virtualDesktopManager;
        _runtimeState = runtimeState;
        _eventLog = eventLog;
        _ignoredProcesses = settings.IgnoredProcesses
            .Select(NormalizeProcessName)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _trackingTimeout = TimeSpan.FromSeconds(Math.Max(1, settings.TrackingTimeoutSeconds));
    }

    public int TrackedProcessCount => _launchRecords.Count;

    public bool TryGetLaunchRecord(int processId, out LaunchRecord? record)
    {
        if (!_launchRecords.TryGetValue(processId, out record))
        {
            return false;
        }

        if (record is null)
        {
            return false;
        }

        if (!IsExpired(record, DateTime.UtcNow))
        {
            return true;
        }

        RemoveExpiredRecord(processId, record, "lookup");
        record = null;
        return false;
    }

    public bool TryMarkWindowProcessed(int processId)
    {
        while (true)
        {
            if (!_launchRecords.TryGetValue(processId, out var existingRecord) || existingRecord is null)
            {
                return false;
            }

            if (IsExpired(existingRecord, DateTime.UtcNow))
            {
                RemoveExpiredRecord(processId, existingRecord, "mark-processed");
                return false;
            }

            if (existingRecord.WindowProcessed)
            {
                return true;
            }

            var updatedRecord = new LaunchRecord
            {
                ProcessId = existingRecord.ProcessId,
                OriginDesktopId = existingRecord.OriginDesktopId,
                LaunchTimeUtc = existingRecord.LaunchTimeUtc,
                WindowProcessed = true
            };

            if (_launchRecords.TryUpdate(processId, updatedRecord, existingRecord))
            {
                return true;
            }
        }
    }

    public bool TryRegisterProcessedWindow(int processId, IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return false;
        }

        if (!_launchRecords.TryGetValue(processId, out var launchRecord) || launchRecord is null)
        {
            return false;
        }

        if (IsExpired(launchRecord, DateTime.UtcNow))
        {
            RemoveExpiredRecord(processId, launchRecord, "register-window");
            return false;
        }

        var processedWindows = _processedWindowsByProcess.GetOrAdd(processId, static _ => new());
        return processedWindows.TryAdd(windowHandle, 0);
    }

    public bool IsWindowProcessed(int processId, IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return false;
        }

        if (!_launchRecords.TryGetValue(processId, out var launchRecord) || launchRecord is null)
        {
            return false;
        }

        if (IsExpired(launchRecord, DateTime.UtcNow))
        {
            RemoveExpiredRecord(processId, launchRecord, "is-window-processed");
            return false;
        }

        if (!_processedWindowsByProcess.TryGetValue(processId, out var processedWindows))
        {
            return false;
        }

        return processedWindows.ContainsKey(windowHandle);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_serviceStarted)
        {
            return Task.CompletedTask;
        }

        _expirationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _expirationTimer = new PeriodicTimer(CleanupSweepInterval);
        _desktopSampleTimer = new PeriodicTimer(DesktopSampleInterval);
        _expirationTask = RunExpirationSweepAsync(_expirationCts.Token);
        _desktopSampleTask = RunDesktopSamplingAsync(_expirationCts.Token);
        _serviceStarted = true;

        if (!TryStartWatcher(enablePrivileges: true, out var privilegedError))
        {
            _logger.LogWarning(
                privilegedError,
                "WMI process launch tracking failed with privileged scope; retrying with standard user scope.");

            if (!TryStartWatcher(enablePrivileges: false, out var standardError))
            {
                _logger.LogError(
                    standardError,
                    "WMI process launch tracking could not start in standard user scope. Launch affinity tracking is disabled.");
            }
        }

        if (!_watcherStarted)
        {
            SeedKnownProcesses();
            _processPollTimer = new PeriodicTimer(ProcessPollInterval);
            _processPollTask = RunProcessPollingAsync(_expirationCts.Token);
            _logger.LogWarning("WMI launch tracking unavailable; using process polling fallback.");
        }

        _logger.LogInformation(
            "Process launch tracker started. TrackingTimeout={TrackingTimeout}, SweepInterval={SweepInterval}, WmiEnabled={WmiEnabled}",
            _trackingTimeout,
            CleanupSweepInterval,
            _watcherStarted);
        _eventLog.Record($"Process tracker started. WMI enabled: {_watcherStarted}.");
        return Task.CompletedTask;
    }

    private bool TryStartWatcher(bool enablePrivileges, out Exception? startupError)
    {
        startupError = null;
        try
        {
            var scope = new ManagementScope(@"\\.\root\CIMV2", new ConnectionOptions
            {
                EnablePrivileges = enablePrivileges
            });
            scope.Connect();

            var query = new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace");
            _watcher = new ManagementEventWatcher(scope, query);
            _watcher.EventArrived += OnProcessStarted;
            _watcher.Start();
            _watcherStarted = true;
            _logger.LogInformation(
                "WMI process launch tracking started. EnablePrivileges={EnablePrivileges}",
                enablePrivileges);
            return true;
        }
        catch (ManagementException ex)
        {
            startupError = ex;
            StopWatcherIfInitialized();
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            startupError = ex;
            StopWatcherIfInitialized();
            return false;
        }
        catch (FileNotFoundException ex)
        {
            startupError = ex;
            StopWatcherIfInitialized();
            return false;
        }
        catch (TypeLoadException ex)
        {
            startupError = ex;
            StopWatcherIfInitialized();
            return false;
        }
    }

    private void StopWatcherIfInitialized()
    {
        if (_watcher is null)
        {
            return;
        }

        _watcher.EventArrived -= OnProcessStarted;
        _watcher.Dispose();
        _watcher = null;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_serviceStarted)
        {
            return;
        }

        if (_watcherStarted && _watcher is not null)
        {
            _watcher.EventArrived -= OnProcessStarted;
            _watcher.Stop();
            _watcher.Dispose();
            _watcher = null;
            _watcherStarted = false;
        }

        if (_expirationCts is not null)
        {
            await _expirationCts.CancelAsync();
        }

        if (_expirationTask is not null)
        {
            await _expirationTask;
        }

        if (_desktopSampleTask is not null)
        {
            await _desktopSampleTask;
        }

        if (_processPollTask is not null)
        {
            await _processPollTask;
        }

        _expirationTimer?.Dispose();
        _expirationTimer = null;
        _desktopSampleTimer?.Dispose();
        _desktopSampleTimer = null;
        _processPollTimer?.Dispose();
        _processPollTimer = null;
        _expirationCts?.Dispose();
        _expirationCts = null;
        _expirationTask = null;
        _desktopSampleTask = null;
        _processPollTask = null;
        _processedWindowsByProcess.Clear();
        _knownProcessIds.Clear();
        lock (_desktopSamplesLock)
        {
            _desktopSamples.Clear();
        }
        _serviceStarted = false;
        _logger.LogInformation("Process launch tracker stopped.");
        _eventLog.Record("Process tracker stopped.");
    }

    public void Dispose()
    {
        if (_watcher is not null)
        {
            _watcher.EventArrived -= OnProcessStarted;
            _watcher.Dispose();
            _watcher = null;
        }

        _expirationTimer?.Dispose();
        _expirationTimer = null;
        _desktopSampleTimer?.Dispose();
        _desktopSampleTimer = null;
        _processPollTimer?.Dispose();
        _processPollTimer = null;
        _expirationCts?.Dispose();
        _expirationCts = null;
        _expirationTask = null;
        _desktopSampleTask = null;
        _processPollTask = null;
        _processedWindowsByProcess.Clear();
        _knownProcessIds.Clear();
        lock (_desktopSamplesLock)
        {
            _desktopSamples.Clear();
        }
        _watcherStarted = false;
        _serviceStarted = false;
    }

    private void OnProcessStarted(object sender, EventArrivedEventArgs args)
    {
        if (!_runtimeState.IsOperational)
        {
            return;
        }

        if (!TryReadProcessStartData(args.NewEvent, out var processId, out var processNameValue, out var eventTimeUtc))
        {
            _logger.LogWarning("Process start event was missing required launch fields.");
            return;
        }

        var normalizedProcessName = NormalizeProcessName(processNameValue);
        if (_ignoredProcesses.Contains(normalizedProcessName))
        {
            _logger.LogDebug(
                "Ignoring process launch for {ProcessName} due to configuration.",
                processNameValue);
            return;
        }

        RegisterLaunchRecord(processId, processNameValue, eventTimeUtc, "wmi");
    }

    private async Task RunProcessPollingAsync(CancellationToken cancellationToken)
    {
        if (_processPollTimer is null)
        {
            return;
        }

        try
        {
            while (await _processPollTimer.WaitForNextTickAsync(cancellationToken))
            {
                PollProcesses();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void PollProcesses()
    {
        var activeProcessIds = new HashSet<int>();
        var processes = Process.GetProcesses();
        try
        {
            foreach (var process in processes)
            {
                using (process)
                {
                    var processId = process.Id;
                    activeProcessIds.Add(processId);
                    if (!_knownProcessIds.TryAdd(processId, 0))
                    {
                        continue;
                    }

                    var processName = process.ProcessName;
                    if (_ignoredProcesses.Contains(NormalizeProcessName(processName)))
                    {
                        continue;
                    }

                    var eventTimeUtc = TryGetProcessStartTimeUtc(process, out var processStartUtc)
                        ? processStartUtc
                        : DateTime.UtcNow;
                    RegisterLaunchRecord(processId, processName, eventTimeUtc, "polling");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Process polling iteration failed.");
        }
        finally
        {
            foreach (var knownProcessId in _knownProcessIds.Keys)
            {
                if (!activeProcessIds.Contains(knownProcessId))
                {
                    _knownProcessIds.TryRemove(knownProcessId, out _);
                }
            }
        }
    }

    private void SeedKnownProcesses()
    {
        var processes = Process.GetProcesses();
        foreach (var process in processes)
        {
            using (process)
            {
                _knownProcessIds.TryAdd(process.Id, 0);
            }
        }
    }

    private void RegisterLaunchRecord(int processId, string processNameValue, DateTime eventTimeUtc, string source)
    {
        var desktopCapturedAtUtc = DateTime.UtcNow;
        var desktopId = _virtualDesktopManager.GetCurrentDesktopId();
        var desktopSource = "current";

        if (TrySelectSampledDesktop(eventTimeUtc, desktopCapturedAtUtc, out var sampledDesktopId, out _, out var sampledSource))
        {
            desktopId = sampledDesktopId;
            desktopSource = sampledSource;
        }

        var record = new LaunchRecord
        {
            ProcessId = processId,
            OriginDesktopId = desktopId,
            LaunchTimeUtc = DateTime.UtcNow,
            WindowProcessed = false
        };

        _launchRecords[processId] = record;
        _processedWindowsByProcess[processId] = new ConcurrentDictionary<IntPtr, byte>();
        _logger.LogInformation(
            "Tracked process launch: PID={ProcessId}, ProcessName={ProcessName}, OriginDesktopId={OriginDesktopId}, DesktopSource={DesktopSource}, EventTimeUtc={EventTimeUtc}, TrackerSource={TrackerSource}, ExpiresAtUtc={ExpiresAtUtc}, TrackedCount={TrackedCount}",
            processId,
            processNameValue,
            desktopId,
            desktopSource,
            eventTimeUtc,
            source,
            record.LaunchTimeUtc.Add(_trackingTimeout),
            _launchRecords.Count);
        _eventLog.Record(
            $"Tracked process launch PID={processId} ({processNameValue}) on desktop {desktopId} (source={desktopSource}, tracker={source}).");
    }

    private static bool TryGetProcessStartTimeUtc(Process process, out DateTime startedUtc)
    {
        startedUtc = default;
        try
        {
            startedUtc = process.StartTime.ToUniversalTime();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task RunDesktopSamplingAsync(CancellationToken cancellationToken)
    {
        if (_desktopSampleTimer is null)
        {
            return;
        }

        try
        {
            while (await _desktopSampleTimer.WaitForNextTickAsync(cancellationToken))
            {
                var sampledAtUtc = DateTime.UtcNow;
                try
                {
                    var desktopId = _virtualDesktopManager.GetCurrentDesktopId();
                    RecordDesktopSample(desktopId, sampledAtUtc);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to sample current virtual desktop.");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void RecordDesktopSample(Guid desktopId, DateTime sampledAtUtc)
    {
        lock (_desktopSamplesLock)
        {
            _desktopSamples.Enqueue(new DesktopSample(desktopId, sampledAtUtc));
            while (_desktopSamples.Count > MaxDesktopSamples)
            {
                _desktopSamples.Dequeue();
            }
        }
    }

    private bool TryGetNearestSampledDesktop(DateTime eventTimeUtc, out Guid desktopId, out DateTime sampledAtUtc)
    {
        desktopId = Guid.Empty;
        sampledAtUtc = default;

        lock (_desktopSamplesLock)
        {
            if (_desktopSamples.Count == 0)
            {
                return false;
            }

            var hasBest = false;
            var bestSample = default(DesktopSample);
            var bestDeltaTicks = long.MaxValue;
            foreach (var sample in _desktopSamples)
            {
                var deltaTicks = Math.Abs((sample.CapturedAtUtc - eventTimeUtc).Ticks);
                if (deltaTicks >= bestDeltaTicks)
                {
                    continue;
                }

                bestDeltaTicks = deltaTicks;
                bestSample = sample;
                hasBest = true;
            }

            if (!hasBest)
            {
                return false;
            }

            desktopId = bestSample.DesktopId;
            sampledAtUtc = bestSample.CapturedAtUtc;
            return true;
        }
    }

    private bool TrySelectSampledDesktop(
        DateTime eventTimeUtc,
        DateTime desktopCapturedAtUtc,
        out Guid desktopId,
        out DateTime sampledAtUtc,
        out string sampledSource)
    {
        desktopId = Guid.Empty;
        sampledAtUtc = default;
        sampledSource = string.Empty;

        if (!TryGetNearestSampledDesktop(eventTimeUtc, out var nearestDesktopId, out var nearestSampledAtUtc))
        {
            return false;
        }

        var hasPriorSample = TryGetMostRecentSampleAtOrBefore(eventTimeUtc, out var priorDesktopId, out var priorSampledAtUtc);
        if (hasPriorSample && eventTimeUtc - priorSampledAtUtc <= MaxPriorDesktopSampleAge)
        {
            desktopId = priorDesktopId;
            sampledAtUtc = priorSampledAtUtc;
            sampledSource = "sampled-prior";
            return true;
        }

        var sampledDelta = GetAbsoluteDuration(nearestSampledAtUtc - eventTimeUtc);
        var currentDelta = GetAbsoluteDuration(desktopCapturedAtUtc - eventTimeUtc);
        if (sampledDelta <= currentDelta + DesktopSampleSelectionBias)
        {
            desktopId = nearestDesktopId;
            sampledAtUtc = nearestSampledAtUtc;
            sampledSource = "sampled-nearest";
            return true;
        }

        return false;
    }

    private bool TryGetMostRecentSampleAtOrBefore(DateTime eventTimeUtc, out Guid desktopId, out DateTime sampledAtUtc)
    {
        desktopId = Guid.Empty;
        sampledAtUtc = default;

        lock (_desktopSamplesLock)
        {
            if (_desktopSamples.Count == 0)
            {
                return false;
            }

            var hasBest = false;
            var bestSample = default(DesktopSample);
            foreach (var sample in _desktopSamples)
            {
                if (sample.CapturedAtUtc > eventTimeUtc)
                {
                    continue;
                }

                if (!hasBest || sample.CapturedAtUtc > bestSample.CapturedAtUtc)
                {
                    bestSample = sample;
                    hasBest = true;
                }
            }

            if (!hasBest)
            {
                return false;
            }

            desktopId = bestSample.DesktopId;
            sampledAtUtc = bestSample.CapturedAtUtc;
            return true;
        }
    }

    private static bool TryReadProcessStartData(
        ManagementBaseObject? processStartEvent,
        out int processId,
        out string processName,
        out DateTime eventTimeUtc)
    {
        processId = 0;
        processName = string.Empty;
        eventTimeUtc = DateTime.UtcNow;

        if (processStartEvent is null)
        {
            return false;
        }

        var processNameValue = processStartEvent["ProcessName"]?.ToString();
        var processIdValue = processStartEvent["ProcessID"];

        if (string.IsNullOrWhiteSpace(processNameValue) || processIdValue is null)
        {
            var targetInstance = processStartEvent["TargetInstance"] as ManagementBaseObject;
            processNameValue ??= targetInstance?["ProcessName"]?.ToString();
            processIdValue ??= targetInstance?["ProcessID"];
        }

        if (string.IsNullOrWhiteSpace(processNameValue) || processIdValue is null)
        {
            return false;
        }

        processId = Convert.ToInt32(processIdValue);
        processName = processNameValue;

        var timeCreatedValue = processStartEvent["TIME_CREATED"];
        if (timeCreatedValue is not null)
        {
            var timeCreated = Convert.ToUInt64(timeCreatedValue);
            if (timeCreated <= long.MaxValue)
            {
                eventTimeUtc = DateTime.FromFileTimeUtc((long)timeCreated);
            }
        }

        return true;
    }

    private static TimeSpan GetAbsoluteDuration(TimeSpan duration)
    {
        return duration < TimeSpan.Zero ? -duration : duration;
    }

    private async Task RunExpirationSweepAsync(CancellationToken cancellationToken)
    {
        if (_expirationTimer is null)
        {
            return;
        }

        try
        {
            while (await _expirationTimer.WaitForNextTickAsync(cancellationToken))
            {
                RunCleanupSweep("periodic-sweep");
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void RunCleanupSweep(string reason)
    {
        var utcNow = DateTime.UtcNow;
        var trackedBefore = _launchRecords.Count;
        var removedExpired = 0;
        var removedNotRunning = 0;
        var prunedWindowSets = 0;
        var prunedWindowEntries = 0;

        foreach (var entry in _launchRecords.ToArray())
        {
            if (IsExpired(entry.Value, utcNow))
            {
                if (RemoveRecord(entry.Key, entry.Value, reason + "-expired"))
                {
                    removedExpired++;
                }

                continue;
            }

            if (IsProcessRunning(entry.Key))
            {
                continue;
            }

            if (RemoveRecord(entry.Key, entry.Value, reason + "-process-exited"))
            {
                removedNotRunning++;
            }
        }

        var activeProcessIds = _launchRecords.Keys.ToHashSet();
        foreach (var trackedWindows in _processedWindowsByProcess.ToArray())
        {
            if (activeProcessIds.Contains(trackedWindows.Key))
            {
                continue;
            }

            if (_processedWindowsByProcess.TryRemove(trackedWindows.Key, out var removedWindows))
            {
                prunedWindowSets++;
                prunedWindowEntries += removedWindows.Count;
            }
        }

        var trackedAfter = _launchRecords.Count;
        _logger.LogInformation(
            "Cleanup sweep completed: Reason={Reason}, TrackedBefore={TrackedBefore}, TrackedAfter={TrackedAfter}, RemovedExpired={RemovedExpired}, RemovedExitedProcess={RemovedExitedProcess}, PrunedWindowSets={PrunedWindowSets}, PrunedWindowEntries={PrunedWindowEntries}",
            reason,
            trackedBefore,
            trackedAfter,
            removedExpired,
            removedNotRunning,
            prunedWindowSets,
            prunedWindowEntries);
        _eventLog.Record(
            $"Cleanup sweep: removed {removedExpired} expired, {removedNotRunning} exited, pruned {prunedWindowSets} window sets.");
    }

    private void RemoveExpiredRecord(int processId, LaunchRecord record, string reason)
    {
        _ = RemoveRecord(processId, record, reason);
    }

    private bool RemoveRecord(int processId, LaunchRecord record, string reason)
    {
        if (!_launchRecords.TryRemove(new KeyValuePair<int, LaunchRecord>(processId, record)))
        {
            return false;
        }

        _processedWindowsByProcess.TryRemove(processId, out var processedWindows);
        var processedWindowCount = processedWindows?.Count ?? 0;

        _logger.LogInformation(
            "Launch record removed: PID={ProcessId}, OriginDesktopId={OriginDesktopId}, LaunchTimeUtc={LaunchTimeUtc}, WindowProcessed={WindowProcessed}, ProcessedWindowCount={ProcessedWindowCount}, Reason={Reason}",
            record.ProcessId,
            record.OriginDesktopId,
            record.LaunchTimeUtc,
            record.WindowProcessed,
            processedWindowCount,
            reason);
        _eventLog.Record($"Launch record removed PID={record.ProcessId}. Reason={reason}.");
        return true;
    }

    private bool IsExpired(LaunchRecord record, DateTime utcNow)
    {
        return utcNow - record.LaunchTimeUtc > _trackingTimeout;
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string NormalizeProcessName(string processName)
    {
        var trimmed = processName.Trim();
        return Path.GetFileNameWithoutExtension(trimmed);
    }

    private readonly record struct DesktopSample(Guid DesktopId, DateTime CapturedAtUtc);
}
