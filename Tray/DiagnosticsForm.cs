namespace DesktopTie.Tray;

public sealed class DiagnosticsForm : Form
{
    private readonly IAgentRuntimeState _runtimeState;
    private readonly IAgentEventLog _eventLog;
    private readonly Label _statusLabel;
    private readonly ListBox _eventList;
    private readonly System.Windows.Forms.Timer _refreshTimer;

    public DiagnosticsForm(IAgentRuntimeState runtimeState, IAgentEventLog eventLog)
    {
        _runtimeState = runtimeState;
        _eventLog = eventLog;

        Text = "DesktopTie Diagnostics";
        Width = 820;
        Height = 520;
        StartPosition = FormStartPosition.CenterScreen;

        _statusLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 30,
            Padding = new Padding(8),
        };

        _eventList = new ListBox
        {
            Dock = DockStyle.Fill
        };

        Controls.Add(_eventList);
        Controls.Add(_statusLabel);

        _refreshTimer = new System.Windows.Forms.Timer
        {
            Interval = 1000
        };
        _refreshTimer.Tick += (_, _) => RefreshUi();
        Shown += (_, _) => _refreshTimer.Start();
        FormClosed += (_, _) => _refreshTimer.Stop();
        RefreshUi();
    }

    public void RefreshStatus()
    {
        RefreshUi();
    }

    private void RefreshUi()
    {
        _statusLabel.Text = $"Status: {(_runtimeState.IsEnabled ? "Enabled" : "Disabled")} | {(_runtimeState.IsPaused ? "Paused" : "Running")}";
        _eventList.BeginUpdate();
        try
        {
            _eventList.Items.Clear();
            foreach (var entry in _eventLog.GetRecent(100))
            {
                _eventList.Items.Add(entry);
            }
        }
        finally
        {
            _eventList.EndUpdate();
        }
    }
}
