namespace DesktopTie.Models;

public sealed class LaunchRecord
{
    public int ProcessId { get; init; }

    public Guid OriginDesktopId { get; init; }

    public DateTime LaunchTimeUtc { get; init; }

    public bool WindowProcessed { get; set; }
}
