using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace CodexQuotaWidget.App.Infrastructure;

/// <summary>
/// Reports the taskbar's occupied screen regions so the capsule can be placed in a real
/// free gap. It combines native child-window bounds (task list, tray) with the UI
/// Automation controls (weather, Start, Search, app buttons) that Explorer renders inside
/// its composition surface. The widget's own window and process are excluded by identity,
/// and a missing occupancy answer is never treated as "safe".
/// </summary>
internal interface ITaskbarOccupancyProvider
{
    bool TryGetOccupiedRegions(
        nint taskbarHandle,
        nint excludeHandle,
        out IReadOnlyList<Rectangle> occupiedRegions);
}

internal sealed class TaskbarOccupancyProvider : ITaskbarOccupancyProvider
{
    private const double ContainerWidthRatio = 0.9;
    private const double ContainerHeightRatio = 0.9;

    public bool TryGetOccupiedRegions(
        nint taskbarHandle,
        nint excludeHandle,
        out IReadOnlyList<Rectangle> occupiedRegions)
    {
        var occupied = new List<Rectangle>();
        occupiedRegions = occupied;

        if (taskbarHandle == nint.Zero
            || !TaskbarNativeMethods.IsWindow(taskbarHandle)
            || !TaskbarNativeMethods.GetWindowRect(taskbarHandle, out var taskbarBounds))
        {
            return false;
        }

        var taskbarRectangle = Rectangle.FromLTRB(
            taskbarBounds.Left,
            taskbarBounds.Top,
            taskbarBounds.Right,
            taskbarBounds.Bottom);

        try
        {
            CollectNativeChildren(taskbarHandle, excludeHandle, taskbarRectangle, occupied);
            if (!CollectAutomationControls(taskbarHandle, excludeHandle, taskbarRectangle, occupied))
            {
                return false;
            }
        }
        catch (Exception)
        {
            return false;
        }

        return occupied.Count > 0;
    }

    /// <summary>
    /// Identity-based exclusion: the widget's own process and its own HWND must never be
    /// counted as taskbar occupancy, otherwise the periodic layout pass would treat the
    /// capsule as a button and drift sideways. This deliberately does not exclude by
    /// geometry, so a real button is never hidden from the calculator.
    /// </summary>
    internal static bool ShouldExcludeFromOccupancy(
        int elementProcessId,
        nint elementWindowHandle,
        nint excludeHandle,
        int ownProcessId) =>
        elementProcessId == ownProcessId
        || (excludeHandle != nint.Zero && elementWindowHandle == excludeHandle);

    private static void CollectNativeChildren(
        nint taskbarHandle,
        nint excludeHandle,
        Rectangle taskbar,
        List<Rectangle> occupied)
    {
        var ownProcessId = Environment.ProcessId;
        EnumChildProcedure callback = (childHandle, _) =>
        {
            if (ShouldExcludeFromOccupancy(
                    GetProcessId(childHandle),
                    childHandle,
                    excludeHandle,
                    ownProcessId)
                || !TaskbarNativeMethods.IsWindow(childHandle))
            {
                return true;
            }

            if (!TaskbarNativeMethods.GetWindowRect(childHandle, out var bounds))
            {
                return true;
            }

            var className = GetClassName(childHandle);
            if (className.Contains("Composition", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var rectangle = Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
            if (IsContainerRegion(rectangle, taskbar))
            {
                return true;
            }

            occupied.Add(rectangle);
            return true;
        };

        _ = TaskbarNativeMethods.EnumChildWindows(taskbarHandle, callback, nint.Zero);
        GC.KeepAlive(callback);
    }

    private static bool CollectAutomationControls(
        nint taskbarHandle,
        nint excludeHandle,
        Rectangle taskbar,
        List<Rectangle> occupied)
    {
        AutomationElement root;
        try
        {
            root = AutomationElement.FromHandle(taskbarHandle);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or ArgumentException)
        {
            return false;
        }

        if (root is null)
        {
            return false;
        }

        // Keyboard-focusable elements cover weather, Start, Search, app and tray
        // controls while skipping the taskbar's decorative panes.
        var condition = new AndCondition(
            new PropertyCondition(AutomationElement.IsKeyboardFocusableProperty, true),
            new PropertyCondition(AutomationElement.IsOffscreenProperty, false));

        AutomationElementCollection found;
        try
        {
            found = root.FindAll(TreeScope.Descendants, condition);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException)
        {
            return false;
        }

        var ownProcessId = Environment.ProcessId;
        var added = 0;
        foreach (AutomationElement element in found)
        {
            try
            {
                var current = element.Current;
                if (ShouldExcludeFromOccupancy(
                        current.ProcessId,
                        (nint)current.NativeWindowHandle,
                        excludeHandle,
                        ownProcessId))
                {
                    continue;
                }

                var bounds = current.BoundingRectangle;
                if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
                {
                    continue;
                }

                var rectangle = Rectangle.FromLTRB(
                    (int)Math.Floor(bounds.Left),
                    (int)Math.Floor(bounds.Top),
                    (int)Math.Ceiling(bounds.Right),
                    (int)Math.Ceiling(bounds.Bottom));
                if (IsContainerRegion(rectangle, taskbar))
                {
                    continue;
                }

                occupied.Add(rectangle);
                added++;
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or COMException)
            {
                // A control vanished mid-enumeration; the remaining bounds are still valid.
            }
        }

        // If excluding ourselves removed everything, occupancy is unknown, not safe.
        return added > 0;
    }

    private static bool IsContainerRegion(Rectangle region, Rectangle taskbar) =>
        region.Width >= taskbar.Width * ContainerWidthRatio
        && region.Height >= taskbar.Height * ContainerHeightRatio;

    private static int GetProcessId(nint windowHandle)
    {
        _ = TaskbarNativeMethods.GetWindowThreadProcessId(windowHandle, out var processId);
        return (int)processId;
    }

    private static string GetClassName(nint windowHandle)
    {
        var buffer = new char[256];
        var length = TaskbarNativeMethods.GetClassName(windowHandle, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }
}