namespace DesktopTie.Config;

public sealed class AgentSettings
{
    public const string SectionName = "AgentSettings";

    public int TrackingTimeoutSeconds { get; init; } = 120;

    public int MoveRetries { get; init; } = 5;

    public List<string> IgnoredProcesses { get; init; } = [];

    public bool LoggingEnabled { get; init; } = true;
}
