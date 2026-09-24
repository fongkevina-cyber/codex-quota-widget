using System.Runtime.InteropServices;

namespace CodexQuotaWidget.App.Infrastructure;

[StructLayout(LayoutKind.Sequential)]
internal struct TaskbarRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TaskbarPoint
{
    public int X;
    public int Y;
}

internal delegate bool EnumChildProcedure(nint childHandle, nint parameter);

/// <summary>
/// All user32 entry points used by the taskbar host. EntryPoint is always explicit so the
/// managed method names never have to match the exported names.
/// </summary>
internal static class TaskbarNativeMethods
{
    internal const int StyleIndex = -16;
    internal const int ExtendedStyleIndex = -20;
    internal const int ParentOrOwnerIndex = -8;
    internal const uint OwnerCommand = 4;
    internal const uint ParentAncestor = 1;

    internal const long ChildStyle = 0x40000000L;
    internal const long PopupStyle = unchecked((long)0x80000000L);
    internal const long TopmostExtendedStyle = 0x00000008L;

    internal const uint NoMove = 0x0002;
    internal const uint NoSize = 0x0001;
    internal const uint NoZOrder = 0x0004;
    internal const uint NoActivate = 0x0010;
    internal const uint FrameChanged = 0x0020;
    internal const uint ShowWindowFlag = 0x0040;

    internal const int HideWindow = 0;
    internal const int ShowNoActivate = 4;

    internal const uint RedrawInvalidate = 0x0001;
    internal const uint RedrawAllChildren = 0x0080;
    internal const uint RedrawUpdateNow = 0x0100;
    internal const uint RedrawFrame = 0x0400;

    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", EntryPoint = "GetParent", SetLastError = true)]
    internal static extern nint GetParent(nint windowHandle);

    [DllImport("user32.dll", EntryPoint = "SetParent", SetLastError = true)]
    internal static extern nint SetParent(nint childHandle, nint parentHandle);

    [DllImport("user32.dll", EntryPoint = "IsWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(nint windowHandle);

    [DllImport("user32.dll", EntryPoint = "GetWindowRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(nint windowHandle, out TaskbarRect rectangle);

    [DllImport("user32.dll", EntryPoint = "GetDpiForWindow")]
    internal static extern uint GetDpiForWindow(nint windowHandle);

    [DllImport("user32.dll", EntryPoint = "SetWindowPos", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        nint windowHandle,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll", EntryPoint = "ScreenToClient", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ScreenToClient(nint windowHandle, ref TaskbarPoint point);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    internal static extern nint GetWindowLongPtr(nint windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    internal static extern nint SetWindowLongPtr(nint windowHandle, int index, nint newValue);

    [DllImport("user32.dll", EntryPoint = "GetWindow", SetLastError = true)]
    internal static extern nint GetWindow(nint windowHandle, uint command);

    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);

    [DllImport("user32.dll", EntryPoint = "GetAncestor", SetLastError = true)]
    internal static extern nint GetAncestor(nint windowHandle, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetDesktopWindow")]
    internal static extern nint GetDesktopWindow();

    [DllImport("user32.dll", EntryPoint = "ShowWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(nint windowHandle, int command);

    [DllImport("user32.dll", EntryPoint = "IsWindowVisible")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(nint windowHandle);

    [DllImport("user32.dll", EntryPoint = "RedrawWindow", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RedrawWindow(nint windowHandle, nint updateRectangle, nint updateRegion, uint flags);

    [DllImport("user32.dll", EntryPoint = "EnumChildWindows", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumChildWindows(
        nint parentHandle,
        EnumChildProcedure callback,
        nint parameter);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetClassName(nint windowHandle, [Out] char[] className, int maxCount);
}
