using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CodexQuotaWidget.App.Infrastructure;

/// <summary>
/// How the widget window is presented. <see cref="Floating"/> is the classic desktop
/// capsule; <see cref="Taskbar"/> attaches the same window as a real child of the Windows
/// taskbar without injecting into Explorer or changing system settings.
/// </summary>
public enum WindowHostMode
{
    Floating = 0,
    Taskbar = 1,
}

/// <summary>Outcome of a hosted-window layout pass.</summary>
public enum TaskbarLayoutResult
{
    UpToDate,
    Repositioned,
    TaskbarMoving,
    NoSafeSlot,
    Failure,
}

/// <summary>Recovery decision after Explorer broadcasts a new taskbar.</summary>
internal enum TaskbarRecoveryAction
{
    None,
    Reattach,
    Recreate,
}

internal interface ITaskbarHostNative
{
    nint FindTaskbar();
    nint GetRealParent(nint windowHandle);
    nint GetDesktopHandle();
    nint SetParent(nint childHandle, nint parentHandle);
    bool IsWindow(nint windowHandle);
    bool GetWindowRect(nint windowHandle, out TaskbarRect rectangle);
    uint GetDpiForWindow(nint windowHandle);
    bool MoveWindow(nint windowHandle, int x, int y, int width, int height);
    bool RefreshWindowFrame(nint windowHandle);
    bool SetWindowVisible(nint windowHandle, bool visible);
    bool IsWindowVisible(nint windowHandle);
    bool RaiseAndRedrawWindow(nint windowHandle);
    void ScreenToClient(nint windowHandle, ref TaskbarPoint point);
    nint GetStyle(nint windowHandle);
    nint GetExtendedStyle(nint windowHandle);
    nint SetStyle(nint windowHandle, nint style);
    nint SetExtendedStyle(nint windowHandle, nint extendedStyle);
    nint GetOwner(nint windowHandle);
    nint SetOwner(nint windowHandle, nint ownerHandle);
    int LastWin32Error { get; }
}

/// <summary>
/// Attaches the widget's own window to the Windows taskbar. The window is converted to a
/// real child window (WS_CHILD, no WS_POPUP, no topmost), and every failure path restores
/// the original styles and parent instead of leaving a half-attached window. Attaching the
/// same window again is an idempotent reposition that never overwrites the original
/// floating snapshot.
/// </summary>
public sealed class TaskbarHostService
{
    private const int PositionTolerancePixels = 2;

    private readonly ITaskbarHostNative _native;
    private readonly ITaskbarOccupancyProvider _occupancy;
    private readonly AppLogger _logger;
    private nint _taskbarHandle;
    private nint _hostedHandle;
    private nint _savedStyle;
    private nint _savedExtendedStyle;
    private nint _savedParent;
    private nint _savedOwner;
    private bool _hasSavedState;
    private TaskbarRect? _lastTaskbarBounds;

    public TaskbarHostService(AppLogger logger)
        : this(new Win32TaskbarHostNative(), new TaskbarOccupancyProvider(), logger)
    {
    }

    internal TaskbarHostService(
        ITaskbarHostNative native,
        ITaskbarOccupancyProvider occupancy,
        AppLogger logger)
    {
        _native = native;
        _occupancy = occupancy;
        _logger = logger;
    }

    /// <summary>
    /// True only when the window is a healthy taskbar child: the taskbar is its real
    /// parent and WS_CHILD is present. Use <see cref="IsTaskbarOwned"/> for the weaker
    /// "still needs recovery" state.
    /// </summary>
    public bool IsHosted =>
        _taskbarHandle != nint.Zero
        && _hostedHandle != nint.Zero
        && IsRealChild(_hostedHandle, _taskbarHandle);

    /// <summary>
    /// True while the taskbar is still the window's parent, even if a partially failed
    /// detach left mixed styles. Such a window must never be treated as floating.
    /// </summary>
    public bool IsTaskbarOwned =>
        _taskbarHandle != nint.Zero
        && _hostedHandle != nint.Zero
        && HasTaskbarParent(_hostedHandle, _taskbarHandle);

    /// <summary>
    /// True when an original snapshot is still retained but the window has not been
    /// verified back to its pre-host state. A rollback or detach that only partly applied
    /// leaves this set so the same entry point can retry instead of losing the snapshot.
    /// </summary>
    public bool HasPendingRestoration => _hasSavedState && !IsHosted;

    /// <summary>
    /// True while the taskbar either still owns the window or a restore is outstanding, so
    /// the window must not be treated as a clean floating window yet.
    /// </summary>
    public bool IsTaskbarManaged => IsTaskbarOwned || HasPendingRestoration;

    /// <summary>
    /// False when Explorer restarted and destroyed the attached child window. A false
    /// result tells the shell to recreate the widget instead of reattaching it.
    /// </summary>
    public bool IsHostHandleAlive => _hostedHandle == nint.Zero || _native.IsWindow(_hostedHandle);

    public bool TryAttach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        try
        {
            return AttachToTaskbar(GetHandle(window), window.Width, window.Height);
        }
        catch (Exception ex)
        {
            _logger.Write("taskbar_host_attach_failed", ex);
            return false;
        }
    }

    public bool TryDetach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        try
        {
            return DetachToDesktop(GetHandle(window));
        }
        catch (Exception ex)
        {
            _logger.Write("taskbar_host_detach_failed", ex);
            return false;
        }
    }

    public TaskbarLayoutResult TryReposition(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        try
        {
            return Reposition(GetHandle(window), window.Width, window.Height);
        }
        catch (Exception ex)
        {
            _logger.Write("taskbar_host_reposition_failed", ex);
            return TaskbarLayoutResult.Failure;
        }
    }

    public TaskbarLayoutResult TryRepositionIfNeeded(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        try
        {
            return RepositionIfNeeded(GetHandle(window), window.Width, window.Height);
        }
        catch (Exception ex)
        {
            _logger.Write("taskbar_host_reposition_failed", ex);
            return TaskbarLayoutResult.Failure;
        }
    }

    /// <summary>
    /// Hides a hosted window natively without tearing down WPF's rendered surface, so the
    /// capsule can be shown again with its content intact.
    /// </summary>
    public bool HideHosted(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        try
        {
            return HideHostedCore(GetHandle(window));
        }
        catch (Exception ex)
        {
            _logger.Write("taskbar_host_hide_failed", ex);
            return false;
        }
    }

    /// <summary>
    /// Shows and refreshes a hosted window: raises it above the taskbar's other children
    /// and forces a native repaint so a reparented layered window renders again.
    /// </summary>
    public bool ShowHosted(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        try
        {
            return ShowHostedCore(GetHandle(window));
        }
        catch (Exception ex)
        {
            _logger.Write("taskbar_host_show_failed", ex);
            return false;
        }
    }

    /// <summary>Raises and repaints an already visible hosted window.</summary>
    public bool RefreshHosted(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        try
        {
            return RefreshHostedCore(GetHandle(window));
        }
        catch (Exception ex)
        {
            _logger.Write("taskbar_host_refresh_failed", ex);
            return false;
        }
    }

    internal bool HideHostedCore(nint windowHandle) =>
        HasTaskbarParent(windowHandle, _taskbarHandle)
        && _native.SetWindowVisible(windowHandle, visible: false);

    internal bool ShowHostedCore(nint windowHandle)
    {
        if (!HasTaskbarParent(windowHandle, _taskbarHandle))
        {
            return false;
        }

        _native.SetWindowVisible(windowHandle, visible: true);
        return _native.RaiseAndRedrawWindow(windowHandle);
    }

    internal bool RefreshHostedCore(nint windowHandle) =>
        HasTaskbarParent(windowHandle, _taskbarHandle)
        && _native.RaiseAndRedrawWindow(windowHandle);

    internal static nint GetHandle(Window window) => new WindowInteropHelper(window).Handle;

    /// <summary>
    /// Chooses how to recover after Explorer announces a new taskbar. Kept pure so the
    /// dead-handle branch stays testable without restarting Explorer.
    /// </summary>
    internal static TaskbarRecoveryAction DecideRecovery(
        bool hostIntended,
        bool handleAlive,
        bool hosted)
    {
        if (!hostIntended)
        {
            return TaskbarRecoveryAction.None;
        }

        if (!handleAlive)
        {
            return TaskbarRecoveryAction.Recreate;
        }

        return hosted ? TaskbarRecoveryAction.None : TaskbarRecoveryAction.Reattach;
    }

    internal bool AttachToTaskbar(nint windowHandle, double widthDip, double heightDip)
    {
        if (windowHandle == nint.Zero)
        {
            return false;
        }

        var taskbar = _native.FindTaskbar();
        if (taskbar == nint.Zero || !_native.IsWindow(taskbar))
        {
            _logger.Write("taskbar_host_taskbar_not_found");
            return false;
        }

        // Re-attaching the same window to the same taskbar is an idempotent reposition and
        // must not capture the current (already hosted) styles as the original snapshot.
        if (_hostedHandle == windowHandle
            && _hasSavedState
            && _taskbarHandle == taskbar
            && IsRealChild(windowHandle, taskbar))
        {
            _ = Reposition(windowHandle, widthDip, heightDip);
            return IsRealChild(windowHandle, taskbar);
        }

        // Refuse to attach when no verified free gap exists, so the capsule never covers a
        // weather, Start, Search, app or tray control.
        if (TryComputeScreenPosition(
                taskbar,
                windowHandle,
                widthDip,
                heightDip,
                out _,
                out var clientPoint,
                out var size) != PositionResult.Computed)
        {
            return false;
        }

        var firstAttachForHandle = _hostedHandle != windowHandle || !_hasSavedState;
        nint savedStyle;
        nint savedExtendedStyle;
        nint savedParent;
        nint savedOwner;
        if (firstAttachForHandle)
        {
            savedStyle = _native.GetStyle(windowHandle);
            savedExtendedStyle = _native.GetExtendedStyle(windowHandle);

            // Real parent link, independent of WS_POPUP/WS_CHILD (desktop normalized to 0).
            savedParent = _native.GetRealParent(windowHandle);
            savedOwner = _native.GetOwner(windowHandle);
            WriteWindowProbe("taskbar_host_attach_snapshot", windowHandle);
        }
        else
        {
            // A new taskbar handle replaced the old host: keep the original snapshot.
            savedStyle = _savedStyle;
            savedExtendedStyle = _savedExtendedStyle;
            savedParent = _savedParent;
            savedOwner = _savedOwner;
        }

        _native.SetParent(windowHandle, taskbar);
        _native.SetStyle(
            windowHandle,
            (nint)(((long)savedStyle & ~TaskbarNativeMethods.PopupStyle) | TaskbarNativeMethods.ChildStyle));
        _native.SetExtendedStyle(
            windowHandle,
            (nint)((long)savedExtendedStyle & ~TaskbarNativeMethods.TopmostExtendedStyle));

        _taskbarHandle = taskbar;
        _hostedHandle = windowHandle;
        _savedStyle = savedStyle;
        _savedExtendedStyle = savedExtendedStyle;
        _savedParent = savedParent;
        _savedOwner = savedOwner;
        _hasSavedState = true;

        if (!IsRealChild(windowHandle, taskbar))
        {
            _logger.Write($"taskbar_host_set_parent_failed_{_native.LastWin32Error}");
            // If the rollback also fails, the snapshot is kept so the entry point can retry.
            _ = RestoreCapturedState(windowHandle);
            return false;
        }

        if (!_native.MoveWindow(windowHandle, clientPoint.X, clientPoint.Y, size.Width, size.Height)
            || !IsRealChild(windowHandle, taskbar))
        {
            _logger.Write("taskbar_host_position_failed");
            _ = RestoreCapturedState(windowHandle);
            return false;
        }

        if (_native.GetWindowRect(taskbar, out var settledBounds))
        {
            _lastTaskbarBounds = settledBounds;
        }

        WriteWindowProbe("taskbar_host_attach_ok", windowHandle);
        return true;
    }

    internal bool DetachToDesktop(nint windowHandle)
    {
        if (windowHandle == nint.Zero)
        {
            return false;
        }

        if (!_hasSavedState)
        {
            // Nothing was captured; only succeed when it is clearly not a taskbar child.
            return _native.IsWindow(windowHandle)
                && ((long)_native.GetStyle(windowHandle) & TaskbarNativeMethods.ChildStyle) == 0
                && _native.GetRealParent(windowHandle) == nint.Zero;
        }

        WriteWindowProbe("taskbar_host_detach_before", windowHandle);
        var restored = RestoreCapturedState(windowHandle);
        WriteWindowProbe(restored ? "taskbar_host_detach_ok" : "taskbar_host_detach_bad", windowHandle);
        return restored;
    }

    /// <summary>
    /// Applies the retained original snapshot and verifies it against real Win32 semantics.
    /// On success the snapshot is cleared; on failure it is deliberately kept so a later
    /// attach/detach call on the same window can retry the recovery.
    /// </summary>
    private bool RestoreCapturedState(nint windowHandle)
    {
        if (!_hasSavedState || _hostedHandle != windowHandle)
        {
            return false;
        }

        // Restore the real parent link, then styles, then the exact owner. A failed
        // SetParent leaves the link untouched, so verification below fails on every retry
        // until the fault is gone.
        _native.SetParent(windowHandle, _savedParent);
        _native.SetStyle(windowHandle, _savedStyle);
        _native.SetExtendedStyle(windowHandle, _savedExtendedStyle);
        if (((long)_savedStyle & TaskbarNativeMethods.ChildStyle) == 0)
        {
            // Restore the exact owner, including clearing any owner WPF installed.
            _native.SetOwner(windowHandle, _savedOwner);
        }

        _native.RefreshWindowFrame(windowHandle);

        if (!VerifyRestored(windowHandle, out var failure))
        {
            _logger.Write($"taskbar_host_restore_pending_{failure}");
            return false;
        }

        ResetState();
        return true;
    }

    /// <summary>
    /// Verifies the restored state against the real parent link (not the WS_POPUP/WS_CHILD
    /// ambiguous GetParent), the exact owner, and the managed style bits.
    /// </summary>
    private bool VerifyRestored(nint windowHandle, out string failure)
    {
        var alive = _native.IsWindow(windowHandle);
        var style = _native.GetStyle(windowHandle);
        var extendedStyle = _native.GetExtendedStyle(windowHandle);
        var parent = _native.GetRealParent(windowHandle);
        var owner = _native.GetOwner(windowHandle);

        var wasChild = ((long)_savedStyle & TaskbarNativeMethods.ChildStyle) != 0;
        var wasPopup = ((long)_savedStyle & TaskbarNativeMethods.PopupStyle) != 0;

        var childMatches = (((long)style & TaskbarNativeMethods.ChildStyle) != 0) == wasChild;
        var popupMatches = (((long)style & TaskbarNativeMethods.PopupStyle) != 0) == wasPopup;
        var topmostMatches = (((long)extendedStyle & TaskbarNativeMethods.TopmostExtendedStyle) != 0)
            == (((long)_savedExtendedStyle & TaskbarNativeMethods.TopmostExtendedStyle) != 0);
        var parentMatches = parent == _savedParent;
        var ownerMatches = owner == _savedOwner;

        failure = !alive
            ? "alive"
            : !childMatches
                ? "child"
                : !popupMatches
                    ? "popup"
                    : !topmostMatches
                        ? "topmost"
                        : !parentMatches
                            ? "parent"
                            : !ownerMatches
                                ? "owner"
                                : string.Empty;

        return failure.Length == 0;
    }

    internal TaskbarLayoutResult Reposition(nint windowHandle, double widthDip, double heightDip)
    {
        if (!HasTaskbarParent(windowHandle, _taskbarHandle))
        {
            return TaskbarLayoutResult.Failure;
        }

        var computed = TryComputeScreenPosition(
            _taskbarHandle,
            windowHandle,
            widthDip,
            heightDip,
            out _,
            out var clientPoint,
            out var size);
        if (computed == PositionResult.NoSafeSlot)
        {
            return TaskbarLayoutResult.NoSafeSlot;
        }

        if (computed != PositionResult.Computed)
        {
            return TaskbarLayoutResult.Failure;
        }

        if (!_native.MoveWindow(windowHandle, clientPoint.X, clientPoint.Y, size.Width, size.Height))
        {
            return TaskbarLayoutResult.Failure;
        }

        if (_native.GetWindowRect(_taskbarHandle, out var settledBounds))
        {
            _lastTaskbarBounds = settledBounds;
        }

        return TaskbarLayoutResult.Repositioned;
    }

    internal TaskbarLayoutResult RepositionIfNeeded(nint windowHandle, double widthDip, double heightDip)
    {
        if (!HasTaskbarParent(windowHandle, _taskbarHandle))
        {
            return TaskbarLayoutResult.Failure;
        }

        if (!_native.GetWindowRect(_taskbarHandle, out var taskbarBounds))
        {
            return TaskbarLayoutResult.Failure;
        }

        // While the taskbar is sliding for auto-hide (or the display is settling) its
        // bounds change. Adopt the new rect so the next stable pass can resume instead of
        // skipping forever, and leave the child alone this time so it follows the parent.
        if (_lastTaskbarBounds is { } last && !IsSameBounds(last, taskbarBounds))
        {
            _lastTaskbarBounds = taskbarBounds;
            _logger.Write("taskbar_host_taskbar_moving");
            return TaskbarLayoutResult.TaskbarMoving;
        }

        var computed = TryComputeScreenPosition(
            _taskbarHandle,
            windowHandle,
            widthDip,
            heightDip,
            out var screenPosition,
            out var clientPoint,
            out var size);
        if (computed == PositionResult.NoSafeSlot)
        {
            return TaskbarLayoutResult.NoSafeSlot;
        }

        if (computed != PositionResult.Computed)
        {
            return TaskbarLayoutResult.Failure;
        }

        if (!_native.GetWindowRect(windowHandle, out var current))
        {
            return TaskbarLayoutResult.Failure;
        }

        var unchanged = Math.Abs(current.Left - screenPosition.Left) <= PositionTolerancePixels
            && Math.Abs(current.Top - screenPosition.Top) <= PositionTolerancePixels;
        if (unchanged)
        {
            _lastTaskbarBounds = taskbarBounds;
            return TaskbarLayoutResult.UpToDate;
        }

        if (!_native.MoveWindow(windowHandle, clientPoint.X, clientPoint.Y, size.Width, size.Height))
        {
            return TaskbarLayoutResult.Failure;
        }

        _lastTaskbarBounds = taskbarBounds;
        return TaskbarLayoutResult.Repositioned;
    }

    private void ResetState()
    {
        _taskbarHandle = nint.Zero;
        _hostedHandle = nint.Zero;
        _savedStyle = nint.Zero;
        _savedExtendedStyle = nint.Zero;
        _savedParent = nint.Zero;
        _savedOwner = nint.Zero;
        _hasSavedState = false;
        _lastTaskbarBounds = null;
    }

    private bool HasTaskbarParent(nint windowHandle, nint taskbar) =>
        windowHandle != nint.Zero
        && taskbar != nint.Zero
        && _native.IsWindow(windowHandle)
        && _native.GetRealParent(windowHandle) == taskbar;

    private bool IsRealChild(nint windowHandle, nint taskbar) =>
        HasTaskbarParent(windowHandle, taskbar)
        && ((long)_native.GetStyle(windowHandle) & TaskbarNativeMethods.ChildStyle) != 0;

    /// <summary>
    /// Minimal, non-sensitive diagnostics for real-machine validation: only HWND, style bits,
    /// parent and owner, never content or account data.
    /// </summary>
    private void WriteWindowProbe(string eventPrefix, nint windowHandle)
    {
        var alive = _native.IsWindow(windowHandle);
        var style = _native.GetStyle(windowHandle);
        var extendedStyle = _native.GetExtendedStyle(windowHandle);
        var parent = _native.GetRealParent(windowHandle);
        var desktop = _native.GetDesktopHandle();
        var owner = _native.GetOwner(windowHandle);
        var nativeVisible = _native.IsWindowVisible(windowHandle);
        _logger.Write($"{eventPrefix}_alive{(alive ? 1 : 0)}_vis{(nativeVisible ? 1 : 0)}_hwnd{unchecked((ulong)windowHandle):x}");
        _logger.Write($"{eventPrefix}_style{unchecked((ulong)style):x}_ex{unchecked((ulong)extendedStyle):x}");
        _logger.Write($"{eventPrefix}_parent{unchecked((ulong)parent):x}_owner{unchecked((ulong)owner):x}");
        _logger.Write($"{eventPrefix}_desktop{unchecked((ulong)desktop):x}_err{_native.LastWin32Error:x}");
    }

    private static bool IsSameBounds(TaskbarRect left, TaskbarRect right) =>
        left.Left == right.Left
        && left.Top == right.Top
        && left.Right == right.Right
        && left.Bottom == right.Bottom;

    private PositionResult TryComputeScreenPosition(
        nint taskbar,
        nint excludeHandle,
        double widthDip,
        double heightDip,
        out (int Left, int Top) screenPosition,
        out TaskbarPoint clientPoint,
        out (int Width, int Height) size)
    {
        screenPosition = default;
        clientPoint = default;
        size = default;

        if (!_native.GetWindowRect(taskbar, out var taskbarBounds))
        {
            _logger.Write("taskbar_host_taskbar_bounds_failed");
            return PositionResult.Failed;
        }

        var dpi = _native.GetDpiForWindow(taskbar);
        size = WindowPlacementService.CalculatePhysicalSize(widthDip, heightDip, dpi);

        if (!_occupancy.TryGetOccupiedRegions(taskbar, excludeHandle, out var occupied))
        {
            _logger.Write("taskbar_host_occupancy_unavailable");
            return PositionResult.Failed;
        }

        var taskbarRectangle = Rectangle.FromLTRB(
            taskbarBounds.Left,
            taskbarBounds.Top,
            taskbarBounds.Right,
            taskbarBounds.Bottom);

        if (!TaskbarLayoutCalculator.TryCalculateHostedPosition(
                taskbarRectangle,
                occupied,
                size.Width,
                size.Height,
                out screenPosition))
        {
            _logger.Write("taskbar_host_no_safe_slot");
            return PositionResult.NoSafeSlot;
        }

        // SetWindowPos for a child window uses the parent's client coordinates.
        clientPoint = new TaskbarPoint { X = screenPosition.Left, Y = screenPosition.Top };
        _native.ScreenToClient(taskbar, ref clientPoint);
        return PositionResult.Computed;
    }

    private enum PositionResult
    {
        Computed,
        NoSafeSlot,
        Failed,
    }

    private sealed class Win32TaskbarHostNative : ITaskbarHostNative
    {
        public nint FindTaskbar() => TaskbarNativeMethods.FindWindow("Shell_TrayWnd", null);

        public int LastWin32Error { get; private set; }

        public nint GetRealParent(nint windowHandle)
        {
            var parent = TaskbarNativeMethods.GetAncestor(windowHandle, TaskbarNativeMethods.ParentAncestor);
            return parent == TaskbarNativeMethods.GetDesktopWindow() ? nint.Zero : parent;
        }

        public nint GetDesktopHandle() => TaskbarNativeMethods.GetDesktopWindow();

        public nint SetParent(nint childHandle, nint parentHandle)
        {
            Marshal.SetLastPInvokeError(0);
            var previous = TaskbarNativeMethods.SetParent(childHandle, parentHandle);
            LastWin32Error = Marshal.GetLastPInvokeError();
            return previous;
        }

        public bool IsWindow(nint windowHandle) => TaskbarNativeMethods.IsWindow(windowHandle);

        public bool GetWindowRect(nint windowHandle, out TaskbarRect rectangle) =>
            TaskbarNativeMethods.GetWindowRect(windowHandle, out rectangle);

        public uint GetDpiForWindow(nint windowHandle)
        {
            var dpi = TaskbarNativeMethods.GetDpiForWindow(windowHandle);
            return dpi == 0 ? 96u : dpi;
        }

        public bool MoveWindow(nint windowHandle, int x, int y, int width, int height) =>
            TaskbarNativeMethods.SetWindowPos(
                windowHandle,
                nint.Zero,
                x,
                y,
                width,
                height,
                TaskbarNativeMethods.NoActivate | TaskbarNativeMethods.FrameChanged);

        public bool RefreshWindowFrame(nint windowHandle) =>
            TaskbarNativeMethods.SetWindowPos(
                windowHandle,
                nint.Zero,
                0,
                0,
                0,
                0,
                TaskbarNativeMethods.NoMove
                    | TaskbarNativeMethods.NoSize
                    | TaskbarNativeMethods.NoZOrder
                    | TaskbarNativeMethods.NoActivate
                    | TaskbarNativeMethods.FrameChanged);

        public bool SetWindowVisible(nint windowHandle, bool visible) =>
            TaskbarNativeMethods.ShowWindow(
                windowHandle,
                visible ? TaskbarNativeMethods.ShowNoActivate : TaskbarNativeMethods.HideWindow);

        public bool IsWindowVisible(nint windowHandle) =>
            TaskbarNativeMethods.IsWindowVisible(windowHandle);

        public bool RaiseAndRedrawWindow(nint windowHandle)
        {
            // HWND_TOP + FRAMECHANGED re-establishes the child z-order, SHOWWINDOW makes the
            // native visibility explicit, and RedrawWindow forces a WM_PAINT of the window
            // and its children so the layered surface is recomposed after reparenting.
            var raised = TaskbarNativeMethods.SetWindowPos(
                windowHandle,
                nint.Zero,
                0,
                0,
                0,
                0,
                TaskbarNativeMethods.NoMove
                    | TaskbarNativeMethods.NoSize
                    | TaskbarNativeMethods.NoActivate
                    | TaskbarNativeMethods.FrameChanged
                    | TaskbarNativeMethods.ShowWindowFlag);
            var redrawn = TaskbarNativeMethods.RedrawWindow(
                windowHandle,
                nint.Zero,
                nint.Zero,
                TaskbarNativeMethods.RedrawInvalidate
                    | TaskbarNativeMethods.RedrawFrame
                    | TaskbarNativeMethods.RedrawAllChildren
                    | TaskbarNativeMethods.RedrawUpdateNow);
            return raised || redrawn;
        }

        public void ScreenToClient(nint windowHandle, ref TaskbarPoint point) =>
            _ = TaskbarNativeMethods.ScreenToClient(windowHandle, ref point);

        public nint GetStyle(nint windowHandle) =>
            TaskbarNativeMethods.GetWindowLongPtr(windowHandle, TaskbarNativeMethods.StyleIndex);

        public nint GetExtendedStyle(nint windowHandle) =>
            TaskbarNativeMethods.GetWindowLongPtr(windowHandle, TaskbarNativeMethods.ExtendedStyleIndex);

        public nint SetStyle(nint windowHandle, nint style) =>
            TaskbarNativeMethods.SetWindowLongPtr(windowHandle, TaskbarNativeMethods.StyleIndex, style);

        public nint SetExtendedStyle(nint windowHandle, nint extendedStyle) =>
            TaskbarNativeMethods.SetWindowLongPtr(windowHandle, TaskbarNativeMethods.ExtendedStyleIndex, extendedStyle);

        public nint GetOwner(nint windowHandle) =>
            TaskbarNativeMethods.GetWindow(windowHandle, TaskbarNativeMethods.OwnerCommand);

        public nint SetOwner(nint windowHandle, nint ownerHandle) =>
            TaskbarNativeMethods.SetWindowLongPtr(
                windowHandle,
                TaskbarNativeMethods.ParentOrOwnerIndex,
                ownerHandle);
    }
}
