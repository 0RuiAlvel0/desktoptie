using System.Reflection;
using Microsoft.Win32;
using System.Runtime.Versioning;
using System.Text;

namespace DesktopTie.Startup;

[SupportedOSPlatform("windows")]
public sealed class StartupRegistrationManager : IStartupRegistrationManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupApprovedRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string RunValueName = "DesktopTie";

    public StartupRegistrationResult GetStatus()
    {
        var entryPath = GetRunKeyEntryPath();
        var hasRunEntry = TryReadRunValue(out _);
        var startupApprovedState = GetStartupApprovedState();
        var enabled = hasRunEntry && startupApprovedState != StartupApprovedState.Disabled;
        return new StartupRegistrationResult
        {
            Action = StartupRegistrationAction.Status,
            Enabled = enabled,
            EntryPath = entryPath,
            Message = enabled
                ? $"DesktopTie startup registration is enabled at: {entryPath}"
                : $"DesktopTie startup registration is disabled. Expected entry path: {entryPath}"
        };
    }

    public StartupRegistrationResult Enable()
    {
        var launcherCommand = BuildLauncherCommand();
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Could not open the current user's Run registry key.");
        key.SetValue(RunValueName, launcherCommand, RegistryValueKind.String);
        ClearStartupApprovedValue();

        return new StartupRegistrationResult
        {
            Action = StartupRegistrationAction.Enable,
            Enabled = true,
            EntryPath = GetRunKeyEntryPath(),
            Message = $"DesktopTie startup registration enabled in Run key: {GetRunKeyEntryPath()}"
        };
    }

    public StartupRegistrationResult Disable()
    {
        var entryPath = GetRunKeyEntryPath();
        EnsureRunEntryExists();
        SetStartupApprovedDisabled();

        return new StartupRegistrationResult
        {
            Action = StartupRegistrationAction.Disable,
            Enabled = false,
            EntryPath = entryPath,
            Message = $"DesktopTie startup registration disabled. Startup entry remains at: {entryPath}"
        };
    }

    private static string GetRunKeyEntryPath()
    {
        return $@"HKCU\\{RunKeyPath}\\{RunValueName}";
    }

    private static string BuildLauncherCommand()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            throw new InvalidOperationException("Could not resolve current process path for startup registration.");
        }

        if (!File.Exists(processPath))
        {
            throw new FileNotFoundException("Resolved process path does not exist.", processPath);
        }

        var processName = Path.GetFileNameWithoutExtension(processPath);
        if (!string.Equals(processName, "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return $"{Quote(processPath)} --tray";
        }

        var entryAssemblyName = Assembly.GetEntryAssembly()?.GetName().Name;
        if (string.IsNullOrWhiteSpace(entryAssemblyName))
        {
            throw new InvalidOperationException("Could not resolve entry assembly name for dotnet startup registration.");
        }

        var exePath = Path.Combine(AppContext.BaseDirectory, $"{entryAssemblyName}.exe");
        if (File.Exists(exePath))
        {
            return $"{Quote(exePath)} --tray";
        }

        var dllPath = Path.Combine(AppContext.BaseDirectory, $"{entryAssemblyName}.dll");
        if (!File.Exists(dllPath))
        {
            throw new FileNotFoundException("Could not resolve entry assembly binary for startup registration.", dllPath);
        }

        return $"{Quote(processPath)} {Quote(dllPath)} --tray";
    }

    private static bool TryReadRunValue(out object? value)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        value = key?.GetValue(RunValueName);
        return value is string stringValue && !string.IsNullOrWhiteSpace(stringValue);
    }

    private static void EnsureRunEntryExists()
    {
        if (TryReadRunValue(out _))
        {
            return;
        }

        var launcherCommand = BuildLauncherCommand();
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Could not open the current user's Run registry key.");
        key.SetValue(RunValueName, launcherCommand, RegistryValueKind.String);
    }

    private static StartupApprovedState GetStartupApprovedState()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupApprovedRunKeyPath, writable: false);
        if (key?.GetValue(RunValueName) is not byte[] rawValue || rawValue.Length == 0)
        {
            return StartupApprovedState.NotConfigured;
        }

        return rawValue[0] switch
        {
            3 => StartupApprovedState.Disabled,
            _ => StartupApprovedState.Enabled
        };
    }

    private static void SetStartupApprovedDisabled()
    {
        using var key = Registry.CurrentUser.CreateSubKey(StartupApprovedRunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Could not open the current user's StartupApproved Run registry key.");
        key.SetValue(RunValueName, BuildStartupApprovedDisabledValue(), RegistryValueKind.Binary);
    }

    private static void ClearStartupApprovedValue()
    {
        using var key = Registry.CurrentUser.CreateSubKey(StartupApprovedRunKeyPath, writable: true);
        key?.DeleteValue(RunValueName, throwOnMissingValue: false);
    }

    private static byte[] BuildStartupApprovedDisabledValue()
    {
        // Windows expects a binary payload where byte 0 is the state (3 == disabled).
        // Storing an accompanying FILETIME timestamp keeps parity with Explorer-written values.
        var payload = new byte[12];
        payload[0] = 3;
        var timestamp = DateTime.UtcNow.ToFileTimeUtc();
        var timeBytes = BitConverter.GetBytes(timestamp);
        Buffer.BlockCopy(timeBytes, 0, payload, 4, timeBytes.Length);
        return payload;
    }

    private enum StartupApprovedState
    {
        NotConfigured = 0,
        Enabled = 1,
        Disabled = 2
    }

    private static string Quote(string value) => $"\"{value}\"";
}
