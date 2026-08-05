using DesktopTie.Services;
using DesktopTie.Startup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Drawing;

namespace DesktopTie.Tray;

public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly IHost _host;
    private readonly IAgentRuntimeState _runtimeState;
    private readonly IAgentEventLog _eventLog;
    private readonly IStartupRegistrationManager _startupRegistrationManager;
    private readonly SynchronizationContext? _uiContext;
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _versionItem;
    private readonly ToolStripMenuItem _startupAtLoginItem;
    private readonly ToolStripMenuItem _enableDisableItem;
    private readonly ToolStripMenuItem _pauseResumeItem;
    private readonly ToolStripMenuItem _restartItem;
    private readonly ToolStripMenuItem _diagnosticsItem;
    private readonly ToolStripMenuItem _exitItem;
    private DiagnosticsForm? _diagnosticsForm;
    private readonly string _startupPromptMarkerPath;

    public TrayApplicationContext(IHost host)
    {
        _host = host;
        _runtimeState = host.Services.GetRequiredService<IAgentRuntimeState>();
        _eventLog = host.Services.GetRequiredService<IAgentEventLog>();
        _startupRegistrationManager = host.Services.GetRequiredService<IStartupRegistrationManager>();
        _startupPromptMarkerPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DesktopTie",
            "startup-prompted.marker");
        _uiContext = SynchronizationContext.Current;
        _runtimeState.StateChanged += OnRuntimeStateChanged;

        _statusItem = new ToolStripMenuItem();
        _versionItem = new ToolStripMenuItem($"Version: {AppVersionProvider.GetRunningVersion()}") { Enabled = false };
        _startupAtLoginItem = new ToolStripMenuItem();
        _enableDisableItem = new ToolStripMenuItem();
        _pauseResumeItem = new ToolStripMenuItem();
        _restartItem = new ToolStripMenuItem("Restart", null, (_, _) => Restart());
        _diagnosticsItem = new ToolStripMenuItem("Diagnostics...", null, (_, _) => ShowDiagnostics());
        _exitItem = new ToolStripMenuItem("Exit", null, async (_, _) => await ExitAsync());

        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add(_versionItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(_statusItem);
        contextMenu.Items.Add(_startupAtLoginItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(_enableDisableItem);
        contextMenu.Items.Add(_pauseResumeItem);
        contextMenu.Items.Add(_restartItem);
        contextMenu.Items.Add(_diagnosticsItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(_exitItem);

        _notifyIcon = new NotifyIcon
        {
            Icon = ResolveTrayIcon(),
            Visible = true,
            Text = "DesktopTie",
            ContextMenuStrip = contextMenu
        };
        _notifyIcon.DoubleClick += (_, _) => ShowDiagnostics();

        UpdateMenuText();
        _eventLog.Record("Tray UI started.");
        BootstrapLog.Write("Tray notify icon initialized and visible.");
        _notifyIcon.BalloonTipTitle = "DesktopTie";
        _notifyIcon.BalloonTipText = "DesktopTie is running in the notification area.";
        _notifyIcon.ShowBalloonTip(3000);
        PromptStartupPreferenceIfNeeded();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _runtimeState.StateChanged -= OnRuntimeStateChanged;
            _diagnosticsForm?.Dispose();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }

        base.Dispose(disposing);
    }

    private void OnRuntimeStateChanged(object? sender, EventArgs e)
    {
        if (_uiContext is not null)
        {
            _uiContext.Post(_ => UpdateMenuTextAndDiagnostics(), null);
            return;
        }

        UpdateMenuTextAndDiagnostics();
    }

    private void UpdateMenuTextAndDiagnostics()
    {
        UpdateMenuText();
        _diagnosticsForm?.RefreshStatus();
    }

    private void UpdateMenuText()
    {
        _statusItem.Text = $"Status: {(_runtimeState.IsEnabled ? "Enabled" : "Disabled")} | {(_runtimeState.IsPaused ? "Paused" : "Running")}";

        UpdateStartupMenuText();
        _startupAtLoginItem.Click -= OnStartupAtLoginClicked;
        _startupAtLoginItem.Click += OnStartupAtLoginClicked;

        _enableDisableItem.Text = _runtimeState.IsEnabled ? "Disable" : "Enable";
        _enableDisableItem.Click -= OnEnableDisableClicked;
        _enableDisableItem.Click += OnEnableDisableClicked;

        _pauseResumeItem.Text = _runtimeState.IsPaused ? "Resume" : "Pause";
        _pauseResumeItem.Click -= OnPauseResumeClicked;
        _pauseResumeItem.Click += OnPauseResumeClicked;

        _notifyIcon.Text = $"DesktopTie ({(_runtimeState.IsEnabled ? "Enabled" : "Disabled")})";
    }

    private void UpdateStartupMenuText()
    {
        try
        {
            var enabled = _startupRegistrationManager.GetStatus().Enabled;
            _startupAtLoginItem.Enabled = true;
            _startupAtLoginItem.Text = enabled ? "Disable startup at login" : "Enable startup at login";
        }
        catch (Exception ex)
        {
            _startupAtLoginItem.Enabled = false;
            _startupAtLoginItem.Text = "Startup at login unavailable";
            _eventLog.Record($"Startup registration status check failed: {ex.Message}");
        }
    }

    private void OnStartupAtLoginClicked(object? sender, EventArgs e)
    {
        try
        {
            var status = _startupRegistrationManager.GetStatus();
            var result = status.Enabled
                ? _startupRegistrationManager.Disable()
                : _startupRegistrationManager.Enable();

            _eventLog.Record(result.Message);
            UpdateMenuText();
        }
        catch (Exception ex)
        {
            _eventLog.Record($"Startup registration update failed: {ex.Message}");
            MessageBox.Show(
                text: ex.Message,
                caption: "DesktopTie Startup",
                buttons: MessageBoxButtons.OK,
                icon: MessageBoxIcon.Error);
        }
    }

    private void OnEnableDisableClicked(object? sender, EventArgs e)
    {
        if (_runtimeState.IsEnabled)
        {
            _runtimeState.Disable();
            _eventLog.Record("Monitoring disabled from tray.");
        }
        else
        {
            _runtimeState.Enable();
            _eventLog.Record("Monitoring enabled from tray.");
        }
    }

    private void OnPauseResumeClicked(object? sender, EventArgs e)
    {
        if (_runtimeState.IsPaused)
        {
            _runtimeState.Resume();
            _eventLog.Record("Monitoring resumed from tray.");
        }
        else
        {
            _runtimeState.Pause();
            _eventLog.Record("Monitoring paused from tray.");
        }
    }

    private void ShowDiagnostics()
    {
        if (_diagnosticsForm is null || _diagnosticsForm.IsDisposed)
        {
            _diagnosticsForm = new DiagnosticsForm(_runtimeState, _eventLog);
            _diagnosticsForm.FormClosed += (_, _) => _diagnosticsForm = null;
            _diagnosticsForm.Show();
            return;
        }

        _diagnosticsForm.Activate();
    }

    private void PromptStartupPreferenceIfNeeded()
    {
        try
        {
            if (File.Exists(_startupPromptMarkerPath))
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_startupPromptMarkerPath)!);

            var result = MessageBox.Show(
                text: "Would you like DesktopTie to start automatically when you sign in?",
                caption: "DesktopTie Startup",
                buttons: MessageBoxButtons.YesNo,
                icon: MessageBoxIcon.Question);

            var registrationResult = result == DialogResult.Yes
                ? _startupRegistrationManager.Enable()
                : _startupRegistrationManager.Disable();

            _eventLog.Record(registrationResult.Message);
            BootstrapLog.Write($"Startup preference prompt result: {registrationResult.Message}");
            File.WriteAllText(_startupPromptMarkerPath, DateTime.UtcNow.ToString("O"));
            UpdateMenuText();
        }
        catch (Exception ex)
        {
            _eventLog.Record($"Startup preference prompt failed: {ex.Message}");
            BootstrapLog.WriteException("Startup preference prompt failed.", ex);
        }
    }

    private void Restart()
    {
        _eventLog.Record("Tray requested application restart.");
        Application.Restart();
        ExitThread();
    }

    private async Task ExitAsync()
    {
        _eventLog.Record("Tray requested application exit.");
        BootstrapLog.Write("Tray requested application exit.");
        await _host.StopAsync();
        ExitThread();
    }

    private static Icon ResolveTrayIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "DesktopTieLogo.ico");
        if (File.Exists(iconPath))
        {
            return new Icon(iconPath);
        }

        return SystemIcons.Application;
    }
}
