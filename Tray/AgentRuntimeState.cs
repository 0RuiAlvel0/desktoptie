namespace DesktopTie.Tray;

public sealed class AgentRuntimeState : IAgentRuntimeState
{
    private int _isEnabled = 1;
    private int _isPaused;

    public bool IsEnabled => Volatile.Read(ref _isEnabled) == 1;

    public bool IsPaused => Volatile.Read(ref _isPaused) == 1;

    public bool IsOperational => IsEnabled && !IsPaused;

    public event EventHandler? StateChanged;

    public void Enable()
    {
        var changed = false;
        changed |= SetEnabled(true);
        changed |= SetPaused(false);
        if (changed)
        {
            OnStateChanged();
        }
    }

    public void Disable()
    {
        var changed = false;
        changed |= SetEnabled(false);
        changed |= SetPaused(true);
        if (changed)
        {
            OnStateChanged();
        }
    }

    public void Pause()
    {
        if (SetPaused(true))
        {
            OnStateChanged();
        }
    }

    public void Resume()
    {
        if (SetPaused(false))
        {
            OnStateChanged();
        }
    }

    private bool SetEnabled(bool value)
    {
        var current = IsEnabled;
        if (current == value)
        {
            return false;
        }

        Interlocked.Exchange(ref _isEnabled, value ? 1 : 0);
        return true;
    }

    private bool SetPaused(bool value)
    {
        var current = IsPaused;
        if (current == value)
        {
            return false;
        }

        Interlocked.Exchange(ref _isPaused, value ? 1 : 0);
        return true;
    }

    private void OnStateChanged()
    {
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
