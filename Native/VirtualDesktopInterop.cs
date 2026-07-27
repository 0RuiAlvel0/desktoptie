using System.Runtime.InteropServices;

namespace DesktopTie.Native;

internal static class VirtualDesktopInterop
{
    internal static readonly Guid ClsidImmersiveShell = new("C2F03A33-21F5-47FA-B4BB-156362A2F239");
    internal static readonly Guid ClsidVirtualDesktopManagerInternal = new("C5E0CDCA-7B6E-41B2-9FC4-D93975CC467B");
    internal static readonly Guid ClsidVirtualDesktopManager = new("AA509086-5CA9-4C25-8F95-589D3C07B48A");
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B")]
internal interface IVirtualDesktopManagerCom
{
    bool IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow);

    Guid GetWindowDesktopId(IntPtr topLevelWindow);

    void MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("F31574D6-B682-4CDC-BD56-1827860ABEC6")]
internal interface IVirtualDesktopManagerInternalWin10
{
    int GetCount();

    void MoveViewToDesktop(IntPtr view, IVirtualDesktopWin10 desktop);

    bool CanViewMoveDesktops(IntPtr view);

    IVirtualDesktopWin10 GetCurrentDesktop();

    void GetDesktops(out IObjectArray desktops);
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("FF72FFDD-BE7E-43FC-9C03-AD81681E88E4")]
internal interface IVirtualDesktopWin10
{
    bool IsViewVisible(IntPtr view);

    Guid GetId();
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("53F5CA0B-158F-4124-900C-057158060B27")]
internal interface IVirtualDesktopManagerInternalWin11
{
    int GetCount();

    void MoveViewToDesktop(IntPtr view, IVirtualDesktopWin11 desktop);

    bool CanViewMoveDesktops(IntPtr view);

    IVirtualDesktopWin11 GetCurrentDesktop();

    void GetDesktops(out IObjectArray desktops);
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("3F07F4BE-B107-441A-AF0F-39D82529072C")]
internal interface IVirtualDesktopWin11
{
    bool IsViewVisible(IntPtr view);

    Guid GetId();
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("1841C6D7-4F9D-42C0-AF41-8747538F10E5")]
internal interface IApplicationViewCollection
{
    int GetViews(out IObjectArray array);

    int GetViewsByZOrder(out IObjectArray array);

    int GetViewsByAppUserModelId(string id, out IObjectArray array);

    int GetViewForHwnd(IntPtr hwnd, out IntPtr view);
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9")]
internal interface IObjectArray
{
    void GetCount(out int count);

    void GetAt(int index, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object obj);
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
internal interface IServiceProvider10
{
    [PreserveSig]
    int QueryService(ref Guid service, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object result);
}
