namespace DesktopTie.Startup;

public sealed class StartupRegistrationResult
{
    public required StartupRegistrationAction Action { get; init; }

    public required bool Enabled { get; init; }

    public required string EntryPath { get; init; }

    public required string Message { get; init; }
}
