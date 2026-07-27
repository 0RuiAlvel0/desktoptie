namespace DesktopTie.Tray;

public interface IAgentRuntimeState
{
    bool IsEnabled { get; }

    bool IsPaused { get; }

    bool IsOperational { get; }

    event EventHandler? StateChanged;

    void Enable();

    void Disable();

    void Pause();

    void Resume();
}
