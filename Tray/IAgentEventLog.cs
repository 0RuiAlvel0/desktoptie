namespace DesktopTie.Tray;

public interface IAgentEventLog
{
    void Record(string message);

    IReadOnlyList<string> GetRecent(int maxEntries = 100);
}
