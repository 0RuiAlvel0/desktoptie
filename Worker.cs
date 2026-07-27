using DesktopTie.Config;
using DesktopTie.Desktop;
using DesktopTie.Tray;
using Microsoft.Extensions.Options;

namespace DesktopTie;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly AgentSettings _agentSettings;
    private readonly IVirtualDesktopManager _virtualDesktopManager;
    private readonly IAgentRuntimeState _runtimeState;
    private readonly IAgentEventLog _eventLog;

    public Worker(
        ILogger<Worker> logger,
        IOptions<AgentSettings> agentSettings,
        IVirtualDesktopManager virtualDesktopManager,
        IAgentRuntimeState runtimeState,
        IAgentEventLog eventLog)
    {
        _logger = logger;
        _agentSettings = agentSettings.Value;
        _virtualDesktopManager = virtualDesktopManager;
        _runtimeState = runtimeState;
        _eventLog = eventLog;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var currentDesktopId = _virtualDesktopManager.GetCurrentDesktopId();
        _logger.LogInformation(
            "DesktopTie agent started. TrackingTimeoutSeconds={TrackingTimeoutSeconds}, MoveRetries={MoveRetries}, IgnoredProcessesCount={IgnoredProcessesCount}, LoggingEnabled={LoggingEnabled}, CurrentDesktopId={CurrentDesktopId}",
            _agentSettings.TrackingTimeoutSeconds,
            _agentSettings.MoveRetries,
            _agentSettings.IgnoredProcesses.Count,
            _agentSettings.LoggingEnabled,
            currentDesktopId);
        _eventLog.Record("Agent worker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            if (!_runtimeState.IsOperational)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                continue;
            }

            var observedDesktopId = _virtualDesktopManager.GetCurrentDesktopId();
            if (observedDesktopId != currentDesktopId)
            {
                currentDesktopId = observedDesktopId;
                _logger.LogInformation("Current virtual desktop changed: {CurrentDesktopId}", currentDesktopId);
                _eventLog.Record($"Current virtual desktop changed to {currentDesktopId}.");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }
}
