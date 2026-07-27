using DesktopTie.Native;
using System.Runtime.InteropServices;

namespace DesktopTie.Desktop;

public sealed class VirtualDesktopManager : IVirtualDesktopManager
{
    private const int EAccessDenied = unchecked((int)0x80070005);

    private readonly IVirtualDesktopManagerCom _virtualDesktopManager;
    private readonly Func<Guid> _getCurrentDesktopId;
    private readonly IVirtualDesktopManagerInternalWin11? _virtualDesktopManagerInternalWin11;
    private readonly IVirtualDesktopManagerInternalWin10? _virtualDesktopManagerInternalWin10;
    private readonly IApplicationViewCollection? _applicationViewCollection;

    public VirtualDesktopManager()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DesktopTie requires Windows to access virtual desktop APIs.");
        }

        var desktopManagerType = Type.GetTypeFromCLSID(VirtualDesktopInterop.ClsidVirtualDesktopManager)
            ?? throw new InvalidOperationException("Unable to resolve VirtualDesktopManager COM type.");
        _virtualDesktopManager = (IVirtualDesktopManagerCom)Activator.CreateInstance(desktopManagerType)!;

        var immersiveShellType = Type.GetTypeFromCLSID(VirtualDesktopInterop.ClsidImmersiveShell)
            ?? throw new InvalidOperationException("Unable to resolve ImmersiveShell COM type.");
        var shell = (IServiceProvider10)Activator.CreateInstance(immersiveShellType)!;

        _getCurrentDesktopId = CreateCurrentDesktopIdAccessor(shell, out _virtualDesktopManagerInternalWin11, out _virtualDesktopManagerInternalWin10);
        _applicationViewCollection = CreateApplicationViewCollectionAccessor(shell);
    }

    public Guid GetCurrentDesktopId()
    {
        return _getCurrentDesktopId();
    }

    public Guid GetWindowDesktop(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            throw new ArgumentException("Window handle must not be zero.", nameof(hwnd));
        }

        return _virtualDesktopManager.GetWindowDesktopId(hwnd);
    }

    public void MoveWindowToDesktop(IntPtr hwnd, Guid desktopId)
    {
        if (hwnd == IntPtr.Zero)
        {
            throw new ArgumentException("Window handle must not be zero.", nameof(hwnd));
        }

        if (desktopId == Guid.Empty)
        {
            throw new ArgumentException("Desktop ID must not be empty.", nameof(desktopId));
        }

        try
        {
            _virtualDesktopManager.MoveWindowToDesktop(hwnd, ref desktopId);
            return;
        }
        catch (UnauthorizedAccessException)
        {
            if (TryMoveViewToDesktop(hwnd, desktopId))
            {
                return;
            }

            throw;
        }
        catch (COMException ex) when (ex.HResult == EAccessDenied)
        {
            if (TryMoveViewToDesktop(hwnd, desktopId))
            {
                return;
            }

            throw;
        }
    }

    private static Func<Guid> CreateCurrentDesktopIdAccessor(
        IServiceProvider10 shell,
        out IVirtualDesktopManagerInternalWin11? managerWin11,
        out IVirtualDesktopManagerInternalWin10? managerWin10)
    {
        managerWin11 = null;
        managerWin10 = null;
        var serviceGuid = VirtualDesktopInterop.ClsidVirtualDesktopManagerInternal;

        var win11InterfaceGuid = typeof(IVirtualDesktopManagerInternalWin11).GUID;
        if (TryQueryService(shell, ref serviceGuid, ref win11InterfaceGuid, out var win11Service))
        {
            managerWin11 = (IVirtualDesktopManagerInternalWin11)win11Service;
            var currentManager = managerWin11;
            return () => currentManager.GetCurrentDesktop().GetId();
        }

        var win10InterfaceGuid = typeof(IVirtualDesktopManagerInternalWin10).GUID;
        if (TryQueryService(shell, ref serviceGuid, ref win10InterfaceGuid, out var win10Service))
        {
            managerWin10 = (IVirtualDesktopManagerInternalWin10)win10Service;
            var currentManager = managerWin10;
            return () => currentManager.GetCurrentDesktop().GetId();
        }

        throw new PlatformNotSupportedException(
            "Could not resolve a supported IVirtualDesktopManagerInternal interface for this Windows version.");
    }

    private static IApplicationViewCollection? CreateApplicationViewCollectionAccessor(IServiceProvider10 shell)
    {
        var serviceGuid = typeof(IApplicationViewCollection).GUID;
        var interfaceGuid = typeof(IApplicationViewCollection).GUID;
        if (TryQueryService(shell, ref serviceGuid, ref interfaceGuid, out var service))
        {
            return (IApplicationViewCollection)service;
        }

        return null;
    }

    private bool TryMoveViewToDesktop(IntPtr hwnd, Guid desktopId)
    {
        if (_applicationViewCollection is null)
        {
            return false;
        }

        IntPtr view = IntPtr.Zero;
        try
        {
            var hr = _applicationViewCollection.GetViewForHwnd(hwnd, out view);
            if (hr != 0 || view == IntPtr.Zero)
            {
                return false;
            }

            if (_virtualDesktopManagerInternalWin11 is not null)
            {
                if (!_virtualDesktopManagerInternalWin11.CanViewMoveDesktops(view))
                {
                    return false;
                }

                if (!TryGetDesktopByIdWin11(_virtualDesktopManagerInternalWin11, desktopId, out var desktop))
                {
                    return false;
                }

                try
                {
                    _virtualDesktopManagerInternalWin11.MoveViewToDesktop(view, desktop);
                    return true;
                }
                finally
                {
                    Marshal.ReleaseComObject(desktop);
                }
            }

            if (_virtualDesktopManagerInternalWin10 is not null)
            {
                if (!_virtualDesktopManagerInternalWin10.CanViewMoveDesktops(view))
                {
                    return false;
                }

                if (!TryGetDesktopByIdWin10(_virtualDesktopManagerInternalWin10, desktopId, out var desktop))
                {
                    return false;
                }

                try
                {
                    _virtualDesktopManagerInternalWin10.MoveViewToDesktop(view, desktop);
                    return true;
                }
                finally
                {
                    Marshal.ReleaseComObject(desktop);
                }
            }

            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
        finally
        {
            if (view != IntPtr.Zero)
            {
                Marshal.Release(view);
            }
        }
    }

    private static bool TryGetDesktopByIdWin11(
        IVirtualDesktopManagerInternalWin11 desktopManager,
        Guid desktopId,
        out IVirtualDesktopWin11 desktop)
    {
        desktop = null!;
        desktopManager.GetDesktops(out var desktops);
        try
        {
            desktops.GetCount(out var count);
            var desktopInterfaceGuid = typeof(IVirtualDesktopWin11).GUID;
            for (var i = 0; i < count; i++)
            {
                desktops.GetAt(i, ref desktopInterfaceGuid, out var desktopObject);
                if (desktopObject is not IVirtualDesktopWin11 candidate)
                {
                    continue;
                }

                if (candidate.GetId() == desktopId)
                {
                    desktop = candidate;
                    return true;
                }

                Marshal.ReleaseComObject(candidate);
            }

            return false;
        }
        finally
        {
            Marshal.ReleaseComObject(desktops);
        }
    }

    private static bool TryGetDesktopByIdWin10(
        IVirtualDesktopManagerInternalWin10 desktopManager,
        Guid desktopId,
        out IVirtualDesktopWin10 desktop)
    {
        desktop = null!;
        desktopManager.GetDesktops(out var desktops);
        try
        {
            desktops.GetCount(out var count);
            var desktopInterfaceGuid = typeof(IVirtualDesktopWin10).GUID;
            for (var i = 0; i < count; i++)
            {
                desktops.GetAt(i, ref desktopInterfaceGuid, out var desktopObject);
                if (desktopObject is not IVirtualDesktopWin10 candidate)
                {
                    continue;
                }

                if (candidate.GetId() == desktopId)
                {
                    desktop = candidate;
                    return true;
                }

                Marshal.ReleaseComObject(candidate);
            }

            return false;
        }
        finally
        {
            Marshal.ReleaseComObject(desktops);
        }
    }

    private static bool TryQueryService(
        IServiceProvider10 shell,
        ref Guid serviceGuid,
        ref Guid interfaceGuid,
        out object service)
    {
        var hr = shell.QueryService(ref serviceGuid, ref interfaceGuid, out service);
        if (hr == 0)
        {
            return true;
        }

        if (hr == unchecked((int)0x80004002))
        {
            return false;
        }

        Marshal.ThrowExceptionForHR(hr);
        return false;
    }
}
