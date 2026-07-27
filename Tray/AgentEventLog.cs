using System.Collections.Concurrent;

namespace DesktopTie.Tray;

public sealed class AgentEventLog : IAgentEventLog
{
    private const int MaxEntries = 100;
    private readonly ConcurrentQueue<string> _entries = new();

    public void Record(string message)
    {
        var entry = $"{DateTime.Now:HH:mm:ss} {message}";
        _entries.Enqueue(entry);

        while (_entries.Count > MaxEntries && _entries.TryDequeue(out _))
        {
        }
    }

    public IReadOnlyList<string> GetRecent(int maxEntries = 100)
    {
        return _entries.Reverse().Take(maxEntries).Reverse().ToArray();
    }
}
