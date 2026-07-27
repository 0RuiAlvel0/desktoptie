using System.Reflection;

namespace DesktopTie.Tray;

public static class AppVersionProvider
{
    public static string GetRunningVersion()
    {
        var informational = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            return informational.Split('+')[0];
        }

        var assemblyVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString();
        return string.IsNullOrWhiteSpace(assemblyVersion) ? "unknown" : assemblyVersion;
    }
}
