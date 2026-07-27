namespace DesktopTie.Desktop;

public interface IVirtualDesktopManager
{
    Guid GetCurrentDesktopId();

    Guid GetWindowDesktop(IntPtr hwnd);

    void MoveWindowToDesktop(IntPtr hwnd, Guid desktopId);
}
