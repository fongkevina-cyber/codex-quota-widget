using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CodexQuotaWidget.App.Infrastructure;

internal static class AltTabVisibilityService
{
    private const int ExtendedWindowStyleIndex = -20;
    private const nint ToolWindowStyle = 0x00000080;
    private const nint AppWindowStyle = 0x00040000;

    public static void Exclude(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == nint.Zero)
        {
            return;
        }

        var currentStyle = GetWindowLongPtr(handle, ExtendedWindowStyleIndex);
        var excludedStyle = CalculateExcludedStyle(currentStyle);
        if (excludedStyle != currentStyle)
        {
            _ = SetWindowLongPtr(handle, ExtendedWindowStyleIndex, excludedStyle);
        }
    }

    internal static nint CalculateExcludedStyle(nint style) =>
        (style | ToolWindowStyle) & ~AppWindowStyle;

    internal static bool IsExcluded(nint handle)
    {
        if (handle == nint.Zero)
        {
            return false;
        }

        var style = GetWindowLongPtr(handle, ExtendedWindowStyleIndex);
        return (style & ToolWindowStyle) != 0
            && (style & AppWindowStyle) == 0;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint windowHandle, int index, nint newValue);
}
