using DesktopTie.Config;
using DesktopTie.Desktop;
using DesktopTie.Services;
using DesktopTie.Startup;
using DesktopTie.Tray;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Windows.Forms;

namespace DesktopTie;

internal static class Program
{
    private const string SingleInstanceMutexName = "DesktopTie.SingleInstance";

    [STAThread]
    private static void Main(string[] args)
    {
        BootstrapLog.Initialize();
        BootstrapLog.Write($"DesktopTie starting. Version={AppVersionProvider.GetRunningVersion()}, ProcessPath={Environment.ProcessPath}, BaseDirectory={AppContext.BaseDirectory}, Args=[{string.Join(", ", args)}]");

        try
        {
            var startupAction = StartupCommandParser.Parse(args);
            BootstrapLog.Write($"Startup action parsed: {startupAction}");
            if (startupAction != StartupRegistrationAction.None)
            {
                var startupManager = new StartupRegistrationManager();
                var result = startupAction switch
                {
                    StartupRegistrationAction.Status => startupManager.GetStatus(),
                    StartupRegistrationAction.Enable => startupManager.Enable(),
                    StartupRegistrationAction.Disable => startupManager.Disable(),
                    _ => throw new ArgumentOutOfRangeException(nameof(startupAction), startupAction, "Unsupported startup action.")
                };

                BootstrapLog.Write($"Startup command completed. Enabled={result.Enabled}, Message={result.Message}");
                Console.WriteLine(result.Message);
                return;
            }

            var trayMode = TrayCommandParser.IsTrayMode(args);
            BootstrapLog.Write($"Tray mode resolved: {trayMode}");

            using var singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
            if (trayMode && !createdNew)
            {
                BootstrapLog.Write("Another DesktopTie tray instance is already running. Exiting duplicate launch.");
                MessageBox.Show(
                    text: "DesktopTie is already running. Check the notification area (system tray).",
                    caption: "DesktopTie",
                    buttons: MessageBoxButtons.OK,
                    icon: MessageBoxIcon.Information);
                return;
            }

            using var host = CreateHost(args);
            BootstrapLog.Write("Host created.");

            if (trayMode)
            {
                ApplicationConfiguration.Initialize();
                BootstrapLog.Write("Windows Forms application initialized.");
                host.Start();
                BootstrapLog.Write("Host started.");

                using var trayContext = new TrayApplicationContext(host);
                BootstrapLog.Write("Tray application context created.");
                Application.Run(trayContext);
                BootstrapLog.Write("Application.Run completed.");
                host.StopAsync().GetAwaiter().GetResult();
                BootstrapLog.Write("Host stopped.");
                return;
            }

            host.Run();
        }
        catch (Exception ex)
        {
            BootstrapLog.WriteException("Fatal startup exception.", ex);
            throw;
        }
    }

    private static IHost CreateHost(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        var settingsSection = builder.Configuration.GetSection(AgentSettings.SectionName);
        builder.Services.Configure<AgentSettings>(settingsSection);

        var agentSettings = settingsSection.Get<AgentSettings>() ?? new AgentSettings();
        if (!agentSettings.LoggingEnabled)
        {
            builder.Logging.ClearProviders();
        }

        builder.Services.AddSingleton<IAgentRuntimeState, AgentRuntimeState>();
        builder.Services.AddSingleton<IAgentEventLog, AgentEventLog>();
        builder.Services.AddSingleton<IVirtualDesktopManager, VirtualDesktopManager>();
        builder.Services.AddSingleton<ProcessTracker>();
        builder.Services.AddSingleton<IProcessTracker>(sp => sp.GetRequiredService<ProcessTracker>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ProcessTracker>());
        builder.Services.AddSingleton<WindowWatcher>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<WindowWatcher>());
        builder.Services.AddHostedService<Worker>();
        builder.Services.AddSingleton<IStartupRegistrationManager, StartupRegistrationManager>();

        return builder.Build();
    }
}
