using DesktopTie.Models;

namespace DesktopTie.Services;

public interface IProcessTracker
{
    bool TryGetLaunchRecord(int processId, out LaunchRecord? record);

    bool IsWindowProcessed(int processId, IntPtr windowHandle);

    bool TryRegisterProcessedWindow(int processId, IntPtr windowHandle);

    bool TryMarkWindowProcessed(int processId);

    int TrackedProcessCount { get; }
}
