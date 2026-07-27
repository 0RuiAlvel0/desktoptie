using System.Text;

namespace DesktopTie.Tray;

public static class BootstrapLog
{
    private static readonly object SyncRoot = new();

    public static string LogFilePath { get; } = BuildLogFilePath();

    public static void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogFilePath)!);
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    public static void Write(string message)
    {
        lock (SyncRoot)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogFilePath)!);
            File.AppendAllText(
                LogFilePath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}",
                Encoding.UTF8);
        }
    }

    public static void WriteException(string context, Exception exception)
    {
        Write($"{context}{Environment.NewLine}{exception}");
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            WriteException($"Unhandled exception. IsTerminating={e.IsTerminating}", exception);
            return;
        }

        Write($"Unhandled non-exception object. IsTerminating={e.IsTerminating}, Value={e.ExceptionObject}");
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteException("Unobserved task exception.", e.Exception);
    }

    private static string BuildLogFilePath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = AppContext.BaseDirectory;
        }

        return Path.Combine(root, "DesktopTie", "logs", "startup.log");
    }
}