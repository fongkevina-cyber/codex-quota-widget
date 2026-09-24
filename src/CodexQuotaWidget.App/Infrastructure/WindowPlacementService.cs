using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CodexQuotaWidget.App.Infrastructure;

public sealed class WindowPlacementService
{
    private const uint SetWindowPositionOnly = 0x0001 | 0x0004 | 0x0010;
    private const uint SetWindowPositionAndSize = 0x0004 | 0x0010;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private readonly SettingsService _settingsService;
    private readonly AppLogger _logger;
    private readonly bool _persistSettings;

    public WindowPlacementService(SettingsService settingsService, AppLogger logger, bool persistSettings = true)
    {
        _settingsService = settingsService;
        _logger = logger;
        _persistSettings = persistSettings;
    }

    public void Restore(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            if (!GetWindowRect(handle, out var rectangle))
            {
                return;
            }

            var settings = _settingsService.Current;
            var monitors = GetMonitors();
            var monitor = monitors.FirstOrDefault(candidate =>
                string.Equals(candidate.DeviceName, settings.MonitorDeviceName, StringComparison.OrdinalIgnoreCase))
                ?? monitors.FirstOrDefault(candidate => candidate.IsPrimary)
                ?? monitors.FirstOrDefault()
                ?? throw new InvalidOperationException("Windows did not report an available display monitor.");
            var workingArea = monitor.WorkingArea;
            var preliminaryPosition = CalculateRestorePosition(
                settings.LeftPixels,
                settings.TopPixels,
                workingArea,
                rectangle.Right - rectangle.Left,
                rectangle.Bottom - rectangle.Top);
            SetWindowPos(
                handle,
                IntPtr.Zero,
                preliminaryPosition.Left,
                preliminaryPosition.Top,
                0,
                0,
                SetWindowPositionOnly);

            // Query after the preliminary move so a saved monitor's scale is used. WPF sizes
            // are DIPs, while SetWindowPos and monitor working areas use physical pixels.
            var size = CalculatePhysicalSize(window.Width, window.Height, GetDpiForWindow(handle));
            var finalPosition = CalculateRestorePosition(
                settings.LeftPixels,
                settings.TopPixels,
                workingArea,
                size.Width,
                size.Height);
            SetWindowPos(
                handle,
                IntPtr.Zero,
                finalPosition.Left,
                finalPosition.Top,
                size.Width,
                size.Height,
                SetWindowPositionAndSize);
        }
        catch (Exception ex)
        {
            _logger.Write("window_position_restore_failed", ex);
        }
    }

    public void ConstrainAndSave(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero || !GetWindowRect(handle, out var rectangle))
            {
                return;
            }

            var monitorHandle = MonitorFromRect(ref rectangle, MonitorDefaultToNearest);
            var monitor = GetMonitor(monitorHandle)
                ?? throw new InvalidOperationException("Windows did not return monitor information.");
            var workingArea = monitor.WorkingArea;
            var width = rectangle.Right - rectangle.Left;
            var height = rectangle.Bottom - rectangle.Top;
            var left = Clamp(rectangle.Left, workingArea.Left, workingArea.Right - width);
            var top = Clamp(rectangle.Top, workingArea.Top, workingArea.Bottom - height);

            if (left != rectangle.Left || top != rectangle.Top)
            {
                SetWindowPos(handle, IntPtr.Zero, left, top, 0, 0, SetWindowPositionOnly);
            }

            var settings = _settingsService.Current;
            settings.MonitorDeviceName = monitor.DeviceName;
            settings.LeftPixels = left;
            settings.TopPixels = top;
            if (_persistSettings)
            {
                _settingsService.SaveCurrent();
            }
        }
        catch (Exception ex)
        {
            _logger.Write("window_position_save_failed", ex);
        }
    }

    internal static (int Left, int Top) CalculateRestorePosition(
        int? savedLeft,
        int? savedTop,
        System.Drawing.Rectangle workingArea,
        int windowWidthPixels,
        int windowHeightPixels)
    {
        var width = Math.Max(1, windowWidthPixels);
        var height = Math.Max(1, windowHeightPixels);
        var left = savedLeft ?? (workingArea.Right - width - 20);
        var top = savedTop ?? (workingArea.Bottom - height - 20);
        return (
            Clamp(left, workingArea.Left, workingArea.Right - width),
            Clamp(top, workingArea.Top, workingArea.Bottom - height));
    }

    internal static (int Width, int Height) CalculatePhysicalSize(
        double widthDip,
        double heightDip,
        uint windowDpi)
    {
        var dpi = windowDpi == 0 ? 96u : windowDpi;
        return (
            Math.Max(1, (int)Math.Round(widthDip * dpi / 96d)),
            Math.Max(1, (int)Math.Round(heightDip * dpi / 96d)));
    }

    private static int Clamp(int value, int minimum, int maximum)
    {
        if (maximum < minimum)
        {
            return minimum;
        }

        return Math.Min(Math.Max(value, minimum), maximum);
    }

    private static IReadOnlyList<DisplayMonitor> GetMonitors()
    {
        var monitors = new List<DisplayMonitor>();
        EnumDisplayMonitors(
            IntPtr.Zero,
            IntPtr.Zero,
            (monitorHandle, _, _, _) =>
            {
                var monitor = GetMonitor(monitorHandle);
                if (monitor is not null)
                {
                    monitors.Add(monitor);
                }

                return true;
            },
            IntPtr.Zero);
        return monitors;
    }

    private static DisplayMonitor? GetMonitor(IntPtr monitorHandle)
    {
        if (monitorHandle == IntPtr.Zero)
        {
            return null;
        }

        var info = new MonitorInfo
        {
            Size = (uint)Marshal.SizeOf<MonitorInfo>(),
            DeviceName = string.Empty,
        };
        if (!GetMonitorInfo(monitorHandle, ref info))
        {
            return null;
        }

        return new DisplayMonitor(
            info.DeviceName,
            System.Drawing.Rectangle.FromLTRB(
                info.WorkingArea.Left,
                info.WorkingArea.Top,
                info.WorkingArea.Right,
                info.WorkingArea.Bottom),
            (info.Flags & 1) != 0);
    }

    private sealed record DisplayMonitor(
        string DeviceName,
        System.Drawing.Rectangle WorkingArea,
        bool IsPrimary);

    private delegate bool MonitorEnumerationProcedure(
        IntPtr monitorHandle,
        IntPtr deviceContext,
        IntPtr monitorRectangle,
        IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public uint Size;
        public NativeRectangle MonitorArea;
        public NativeRectangle WorkingArea;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr windowHandle, out NativeRectangle rectangle);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(
        IntPtr deviceContext,
        IntPtr clipRectangle,
        MonitorEnumerationProcedure enumerationProcedure,
        IntPtr data);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(
        ref NativeRectangle rectangle,
        uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(
        IntPtr monitorHandle,
        ref MonitorInfo monitorInfo);
}
