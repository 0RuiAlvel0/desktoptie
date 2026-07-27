using DesktopTie.Config;
using DesktopTie.Desktop;
using DesktopTie.Native;
using DesktopTie.Tray;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DesktopTie.Services;

[SupportedOSPlatform("windows")]
public sealed class WindowWatcher : IHostedService, IDisposable
{
    private const int TypeElementNotFoundHResult = unchecked((int)0x8002802B);
    private static readonly TimeSpan MoveRetryDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan LaunchRecordRetryDelay = TimeSpan.FromMilliseconds(100);
    private const int LaunchRecordRetryCount = 20;

    private readonly ILogger<WindowWatcher> _logger;
    private readonly IVirtualDesktopManager _virtualDesktopManager;
    private readonly IProcessTracker _processTracker;
    private readonly IAgentRuntimeState _runtimeState;
    private readonly IAgentEventLog _eventLog;
    private readonly int _moveRetries;
    private readonly ConcurrentDictionary<IntPtr, byte> _activeHooks = new();
    private readonly ConcurrentDictionary<IntPtr, byte> _inFlightWindows = new();
    private readonly ManualResetEventSlim _messageLoopReady = new(false);
    private readonly WinEventDelegate _winEventCallback;
    private CancellationTokenSource? _processingCts;
    private Thread? _hookThread;
    private uint _hookThreadId;
    private bool _isStarted;

    public WindowWatcher(
        ILogger<WindowWatcher> logger,
        IVirtualDesktopManager virtualDesktopManager,
        IProcessTracker processTracker,
        IAgentRuntimeState runtimeState,
        IAgentEventLog eventLog,
        IOptions<AgentSettings> agentSettings)
    {
        _logger = logger;
        _virtualDesktopManager = virtualDesktopManager;
        _processTracker = processTracker;
        _runtimeState = runtimeState;
        _eventLog = eventLog;
        _moveRetries = Math.Max(1, agentSettings.Value.MoveRetries);
        _winEventCallback = OnWinEvent;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_isStarted)
        {
            return Task.CompletedTask;
        }

        _processingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _hookThread = new Thread(RunHookThread)
        {
            IsBackground = true,
            Name = "DesktopTie.WindowWatcher"
        };
        _hookThread.SetApartmentState(ApartmentState.MTA);
        _hookThread.Start();

        if (!_messageLoopReady.Wait(TimeSpan.FromSeconds(5)))
        {
            throw new InvalidOperationException("Window watcher message loop did not initialize.");
        }

        _isStarted = true;
        _logger.LogInformation("Window watcher started and listening for window creation/show events.");
        _eventLog.Record("Window watcher started.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_isStarted)
        {
            return Task.CompletedTask;
        }

        if (_hookThreadId != 0)
        {
            var posted = User32.PostThreadMessage(_hookThreadId, User32.WmQuit, UIntPtr.Zero, IntPtr.Zero);
            if (!posted)
            {
                var errorCode = Marshal.GetLastWin32Error();
                _logger.LogWarning(
                    "Failed to post WM_QUIT to window watcher thread. Win32Error={Win32Error}",
                    errorCode);
            }
        }

        if (_hookThread is not null && !_hookThread.Join(TimeSpan.FromSeconds(5)))
        {
            _logger.LogWarning("Window watcher thread did not stop within timeout.");
        }

        _processingCts?.Cancel();
        _processingCts?.Dispose();
        _processingCts = null;
        _inFlightWindows.Clear();
        _messageLoopReady.Reset();
        _hookThread = null;
        _hookThreadId = 0;
        _isStarted = false;
        _logger.LogInformation("Window watcher stopped.");
        _eventLog.Record("Window watcher stopped.");
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        foreach (var hook in _activeHooks.Keys)
        {
            User32.UnhookWinEvent(hook);
        }

        _activeHooks.Clear();
        _processingCts?.Cancel();
        _processingCts?.Dispose();
        _processingCts = null;
        _inFlightWindows.Clear();
        _messageLoopReady.Dispose();
    }

    private void RunHookThread()
    {
        _hookThreadId = Kernel32.GetCurrentThreadId();
        User32.PeekMessage(out _, IntPtr.Zero, 0, 0, User32.PmNoremove);

        TryRegisterHook(User32.EventObjectCreate);
        TryRegisterHook(User32.EventObjectShow);

        _messageLoopReady.Set();
        while (User32.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            User32.TranslateMessage(ref msg);
            User32.DispatchMessage(ref msg);
        }

        foreach (var hook in _activeHooks.Keys)
        {
            User32.UnhookWinEvent(hook);
        }

        _activeHooks.Clear();
    }

    private void TryRegisterHook(uint eventType)
    {
        var hook = User32.SetWinEventHook(
            eventType,
            eventType,
            IntPtr.Zero,
            _winEventCallback,
            0,
            0,
            User32.WineventOutofcontext | User32.WineventSkipownprocess);

        if (hook == IntPtr.Zero)
        {
            var errorCode = Marshal.GetLastWin32Error();
            _logger.LogError(
                "Failed to register WinEvent hook for event {EventType}. Win32Error={Win32Error}",
                eventType,
                errorCode);
            return;
        }

        _activeHooks.TryAdd(hook, 0);
    }

    private void OnWinEvent(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint idEventThread,
        uint dwmsEventTime)
    {
        if (!_runtimeState.IsOperational)
        {
            return;
        }

        if (hwnd == IntPtr.Zero)
        {
            _logger.LogDebug("Ignored window event {EventType}: null hwnd.", eventType);
            return;
        }

        if (idObject != User32.ObjIdWindow)
        {
            _logger.LogDebug(
                "Ignored window event {EventType}: unsupported object id {ObjectId}.",
                eventType,
                idObject);
            return;
        }

        if (idChild != User32.ChildIdSelf)
        {
            _logger.LogDebug(
                "Ignored window event {EventType}: unsupported child id {ChildId}.",
                eventType,
                idChild);
            return;
        }

        if (!User32.IsWindowVisible(hwnd))
        {
            _logger.LogDebug("Ignored window {WindowHandle}: not visible.", hwnd);
            return;
        }

        if (User32.GetParent(hwnd) != IntPtr.Zero)
        {
            _logger.LogDebug("Ignored window {WindowHandle}: not top-level.", hwnd);
            return;
        }

        if (User32.GetWindowTextLength(hwnd) <= 0)
        {
            _logger.LogDebug("Ignored window {WindowHandle}: empty title.", hwnd);
            return;
        }

        User32.GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == 0)
        {
            _logger.LogDebug("Ignored window {WindowHandle}: missing process id.", hwnd);
            return;
        }

        _logger.LogInformation(
            "Window discovered: EventType={EventType}, WindowHandle=0x{WindowHandle:x}, ProcessId={ProcessId}",
            eventType,
            hwnd.ToInt64(),
            processId);
        _eventLog.Record($"Window discovered: hwnd=0x{hwnd.ToInt64():x}, pid={processId}.");

        var trackedProcessId = (int)processId;
        if (_processTracker.IsWindowProcessed(trackedProcessId, hwnd))
        {
            _logger.LogDebug(
                "Window {WindowHandle} ignored for affinity move: window already processed for PID={ProcessId}.",
                hwnd,
                trackedProcessId);
            return;
        }

        if (!_inFlightWindows.TryAdd(hwnd, 0))
        {
            _logger.LogDebug(
                "Window {WindowHandle} is already being processed for PID={ProcessId}.",
                hwnd,
                trackedProcessId);
            return;
        }

        var processingTask = ProcessWindowAsync(hwnd, trackedProcessId);
        _ = processingTask.ContinueWith(
            static (task, state) =>
            {
                if (state is not ILogger<WindowWatcher> logger)
                {
                    return;
                }

                logger.LogError(task.Exception, "Unhandled exception while processing window affinity.");
            },
            _logger,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private async Task ProcessWindowAsync(IntPtr hwnd, int processId)
    {
        try
        {
            var token = _processingCts?.Token ?? CancellationToken.None;
            var launchRecord = await WaitForLaunchRecordAsync(processId, token);
            if (launchRecord is null)
            {
                _logger.LogInformation(
                    "Skipping affinity move for window 0x{WindowHandle:x}: launch record not available for PID={ProcessId} after retries.",
                    hwnd.ToInt64(),
                    processId);
                return;
            }

            for (var attempt = 1; attempt <= _moveRetries; attempt++)
            {
                token.ThrowIfCancellationRequested();

                if (!_processTracker.TryGetLaunchRecord(processId, out launchRecord) || launchRecord is null)
                {
                    _logger.LogInformation(
                        "Skipping affinity move for window 0x{WindowHandle:x}: launch record expired or removed for PID={ProcessId}.",
                        hwnd.ToInt64(),
                        processId);
                    return;
                }

                try
                {
                    Guid? windowDesktopId = null;
                    try
                    {
                        windowDesktopId = _virtualDesktopManager.GetWindowDesktop(hwnd);
                        if (windowDesktopId == launchRecord.OriginDesktopId)
                        {
                            _logger.LogInformation(
                                "Window 0x{WindowHandle:x} for PID={ProcessId} already on origin desktop {OriginDesktopId}.",
                                hwnd.ToInt64(),
                                processId,
                                launchRecord.OriginDesktopId);
                            MarkWindowAndLaunchProcessed(processId, hwnd);
                            return;
                        }
                    }
                    catch (COMException ex) when (ex.HResult == TypeElementNotFoundHResult)
                    {
                        _logger.LogDebug(
                            ex,
                            "GetWindowDesktopId returned element-not-found for window 0x{WindowHandle:x} PID={ProcessId} on attempt {Attempt}/{MaxAttempts}; attempting move anyway.",
                            hwnd.ToInt64(),
                            processId,
                            attempt,
                            _moveRetries);
                    }

                    _virtualDesktopManager.MoveWindowToDesktop(hwnd, launchRecord.OriginDesktopId);

                    Guid? movedDesktopId = null;
                    try
                    {
                        movedDesktopId = _virtualDesktopManager.GetWindowDesktop(hwnd);
                    }
                    catch (COMException ex) when (ex.HResult == TypeElementNotFoundHResult)
                    {
                        _logger.LogDebug(
                            ex,
                            "Move verification desktop query returned element-not-found for window 0x{WindowHandle:x} PID={ProcessId} on attempt {Attempt}/{MaxAttempts}; treating move as successful.",
                            hwnd.ToInt64(),
                            processId,
                            attempt,
                            _moveRetries);
                    }

                    if (movedDesktopId is null || movedDesktopId == launchRecord.OriginDesktopId)
                    {
                        _logger.LogInformation(
                            "Moved window 0x{WindowHandle:x} for PID={ProcessId} from desktop {CurrentDesktopId} to origin desktop {OriginDesktopId} on attempt {Attempt}/{MaxAttempts}.",
                            hwnd.ToInt64(),
                            processId,
                            windowDesktopId,
                            launchRecord.OriginDesktopId,
                            attempt,
                            _moveRetries);
                        _eventLog.Record(
                            $"Moved window hwnd=0x{hwnd.ToInt64():x} pid={processId} to desktop {launchRecord.OriginDesktopId} on attempt {attempt}/{_moveRetries}.");
                        MarkWindowAndLaunchProcessed(processId, hwnd);
                        return;
                    }

                    _logger.LogWarning(
                        "Move verification failed for window 0x{WindowHandle:x} and PID={ProcessId} on attempt {Attempt}/{MaxAttempts}. CurrentDesktopId={CurrentDesktopId}, OriginDesktopId={OriginDesktopId}.",
                        hwnd.ToInt64(),
                        processId,
                        attempt,
                        _moveRetries,
                        movedDesktopId,
                        launchRecord.OriginDesktopId);
                }
                catch (COMException ex)
                {
                    _logger.LogWarning(
                        ex,
                        "COM error while moving window 0x{WindowHandle:x} for PID={ProcessId} on attempt {Attempt}/{MaxAttempts}.",
                        hwnd.ToInt64(),
                        processId,
                        attempt,
                        _moveRetries);
                }
                catch (InvalidOperationException ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Invalid operation while moving window 0x{WindowHandle:x} for PID={ProcessId} on attempt {Attempt}/{MaxAttempts}.",
                        hwnd.ToInt64(),
                        processId,
                        attempt,
                        _moveRetries);
                }
                catch (ArgumentException ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Window argument became invalid while moving window 0x{WindowHandle:x} for PID={ProcessId} on attempt {Attempt}/{MaxAttempts}.",
                        hwnd.ToInt64(),
                        processId,
                        attempt,
                        _moveRetries);
                }
                catch (UnauthorizedAccessException ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Access denied while moving window 0x{WindowHandle:x} for PID={ProcessId} on attempt {Attempt}/{MaxAttempts}.",
                        hwnd.ToInt64(),
                        processId,
                        attempt,
                        _moveRetries);
                }

                if (attempt < _moveRetries)
                {
                    await Task.Delay(MoveRetryDelay, token);
                }
            }

            _logger.LogError(
                "Failed to move window 0x{WindowHandle:x} for PID={ProcessId} after {MaxAttempts} attempts.",
                hwnd.ToInt64(),
                processId,
                _moveRetries);
            _eventLog.Record($"Failed to move window hwnd=0x{hwnd.ToInt64():x} pid={processId} after {_moveRetries} attempts.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Unexpected error while processing window 0x{WindowHandle:x} for PID={ProcessId}.",
                hwnd.ToInt64(),
                processId);
        }
        finally
        {
            _inFlightWindows.TryRemove(hwnd, out _);
        }
    }

    private async Task<DesktopTie.Models.LaunchRecord?> WaitForLaunchRecordAsync(int processId, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= LaunchRecordRetryCount; attempt++)
        {
            if (_processTracker.TryGetLaunchRecord(processId, out var launchRecord) && launchRecord is not null)
            {
                return launchRecord;
            }

            if (attempt < LaunchRecordRetryCount)
            {
                await Task.Delay(LaunchRecordRetryDelay, cancellationToken);
            }
        }

        return null;
    }

    private void MarkWindowAndLaunchProcessed(int processId, IntPtr hwnd)
    {
        if (_processTracker.TryRegisterProcessedWindow(processId, hwnd))
        {
            _logger.LogDebug(
                "Marked window 0x{WindowHandle:x} as processed for PID={ProcessId}.",
                hwnd.ToInt64(),
                processId);
        }

        if (_processTracker.TryMarkWindowProcessed(processId))
        {
            _logger.LogInformation(
                "Marked launch record as processed for PID={ProcessId}.",
                processId);
            _eventLog.Record($"Marked PID={processId} launch record processed.");
            return;
        }

        _logger.LogWarning(
            "Failed to mark launch record as processed for PID={ProcessId}.",
            processId);
    }
}

internal static class Kernel32
{
    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();
}
