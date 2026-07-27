namespace DesktopTie.Tray;

public static class TrayCommandParser
{
    public static bool IsTrayMode(string[] args)
    {
        return args.Length == 0
            || args.Any(arg => string.Equals(arg, "--tray", StringComparison.OrdinalIgnoreCase));
    }
}
