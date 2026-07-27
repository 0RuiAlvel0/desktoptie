namespace DesktopTie.Startup;

public static class StartupCommandParser
{
    public static StartupRegistrationAction Parse(string[] args)
    {
        if (args.Length < 2)
        {
            return StartupRegistrationAction.None;
        }

        if (!string.Equals(args[0], "--startup", StringComparison.OrdinalIgnoreCase))
        {
            return StartupRegistrationAction.None;
        }

        return args[1].ToLowerInvariant() switch
        {
            "status" => StartupRegistrationAction.Status,
            "enable" => StartupRegistrationAction.Enable,
            "disable" => StartupRegistrationAction.Disable,
            _ => throw new ArgumentException(
                "Invalid startup action. Use one of: status, enable, disable.",
                nameof(args))
        };
    }
}
