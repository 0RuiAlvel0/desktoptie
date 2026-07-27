using System.Runtime.InteropServices;

namespace DesktopTie.Native;

internal static class User32
{
    internal const uint EventObjectCreate = 0x8000;
    internal const uint EventObjectShow = 0x8002;
    internal const uint WmQuit = 0x0012;
    internal const int ObjIdWindow = 0;
    internal const int ChildIdSelf = 0;
    internal const uint WineventOutofcontext = 0x0000;
    internal const uint WineventSkipownprocess = 0x0002;
    internal const uint PmNoremove = 0x0000;

    [DllImport("user32.dll")]
    internal static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    internal static extern sbyte GetMessage(out NativeMessage lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TranslateMessage([In] ref NativeMessage lpMsg);

    [DllImport("user32.dll")]
    internal static extern IntPtr DispatchMessage([In] ref NativeMessage lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostThreadMessage(uint idThread, uint msg, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PeekMessage(
        out NativeMessage lpMsg,
        IntPtr hWnd,
        uint wMsgFilterMin,
        uint wMsgFilterMax,
        uint wRemoveMsg);
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeMessage
{
    public IntPtr Hwnd;
    public uint Message;
    public UIntPtr WParam;
    public IntPtr LParam;
    public uint Time;
    public NativePoint Point;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePoint
{
    public int X;
    public int Y;
}

internal delegate void WinEventDelegate(
    IntPtr hWinEventHook,
    uint eventType,
    IntPtr hwnd,
    int idObject,
    int idChild,
    uint idEventThread,
    uint dwmsEventTime);
