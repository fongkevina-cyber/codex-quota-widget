using System.Drawing;
using System.Windows.Interop;
using System.Windows.Media;

namespace CodexQuotaWidget.App.Infrastructure;

/// <summary>Real native/occupancy environment for the taskbar capsule host.</summary>
internal sealed class TaskbarCapsuleEnvironment : ITaskbarCapsuleEnvironment
{
    private readonly ITaskbarOccupancyProvider _occupancy = new TaskbarOccupancyProvider();

    public nint FindTaskbar() => TaskbarNativeMethods.FindWindow("Shell_TrayWnd", null);

    public bool IsWindow(nint windowHandle) => TaskbarNativeMethods.IsWindow(windowHandle);

    public bool TryGetBounds(nint windowHandle, out TaskbarRect bounds) =>
        TaskbarNativeMethods.GetWindowRect(windowHandle, out bounds);

    public uint GetDpi(nint taskbarHandle)
    {
        var dpi = TaskbarNativeMethods.GetDpiForWindow(taskbarHandle);
        return dpi == 0 ? 96u : dpi;
    }

    public bool TryGetOccupied(
        nint taskbarHandle,
        nint excludeHandle,
        out IReadOnlyList<Rectangle> occupiedRegions) =>
        _occupancy.TryGetOccupiedRegions(taskbarHandle, excludeHandle, out occupiedRegions);

    public TaskbarPoint ToClient(nint taskbarHandle, int x, int y)
    {
        var point = new TaskbarPoint { X = x, Y = y };
        if (!TaskbarNativeMethods.ScreenToClient(taskbarHandle, ref point))
            throw new InvalidOperationException("Taskbar coordinate conversion failed.");
        return point;
    }

    public nint GetRealParent(nint windowHandle)
    {
        var parent = TaskbarNativeMethods.GetAncestor(windowHandle, TaskbarNativeMethods.ParentAncestor);
        return parent == TaskbarNativeMethods.GetDesktopWindow() ? nint.Zero : parent;
    }
}

/// <summary>
/// Creates the capsule host surface as an HwndSource whose parent is the taskbar from
/// creation. Per-pixel transparency is requested with the child-capable property and then
/// verified against the created source and the real WS_EX_LAYERED style. When it is not
/// actually enabled the surface is disposed and creation fails; the capsule is never
/// silently rendered opaque.
/// </summary>
internal sealed class HwndSourceCapsuleSurfaceFactory : ITaskbarCapsuleSurfaceFactory
{
    public System.Windows.Size Measure(string percentage)
    {
        var view = new TaskbarCapsuleHostView();
        view.SetContent(new { WeeklyRemainingText = percentage });
        return view.FitContent();
    }

    private const int WsChild = 0x40000000;
    private const int WsClipSiblings = 0x04000000;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const long WsExLayered = 0x00080000L;

    public bool TryCreate(
        nint parentHandle,
        int x,
        int y,
        int width,
        int height,
        out ITaskbarCapsuleSurface? surface,
        out string mode,
        out string reason)
    {
        surface = null;
        mode = "none";
        reason = string.Empty;

        HwndSource? source = null;
        try
        {
            var parameters = new HwndSourceParameters("CodexQuotaWidget.TaskbarCapsuleHost.v2")
            {
                ParentWindow = parentHandle,
                WindowStyle = unchecked(WsChild | WsClipSiblings),
                ExtendedWindowStyle = WsExToolWindow | WsExNoActivate,
                PositionX = x,
                PositionY = y,
                Width = Math.Max(1, width),
                Height = Math.Max(1, height),
                UsesPerPixelTransparency = true,
            };

            source = new HwndSource(parameters);

            // Report the values that actually took effect, not the request.
            var sourceOpacity = source.UsesPerPixelOpacity;
            var layered = ((long)TaskbarNativeMethods.GetWindowLongPtr(
                source.Handle,
                TaskbarNativeMethods.ExtendedStyleIndex) & WsExLayered) != 0;
            var actualMode = $"per_pixel_op{(sourceOpacity ? 1 : 0)}_layered{(layered ? 1 : 0)}";

            if (!sourceOpacity || !layered)
            {
                reason = $"transparency_not_enabled_op{(sourceOpacity ? 1 : 0)}_layered{(layered ? 1 : 0)}";
                source.Dispose();
                source = null;
                return false;
            }

            var view = new TaskbarCapsuleHostView();
            source.RootVisual = view;
            if (source.CompositionTarget is not null)
            {
                source.CompositionTarget.BackgroundColor = Colors.Transparent;
            }

            view.UpdateLayout();
            surface = new HwndSourceCapsuleSurface(source, view);
            mode = actualMode;
            source = null;
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.GetType().Name;
            return false;
        }
        finally
        {
            // Ownership only transfers to the surface on success; otherwise release it.
            source?.Dispose();
        }
    }

    private sealed class HwndSourceCapsuleSurface : ITaskbarCapsuleSurface
    {
        private readonly HwndSource _source;
        private readonly TaskbarCapsuleHostView _view;
        private bool _disposed;

        public HwndSourceCapsuleSurface(HwndSource source, TaskbarCapsuleHostView view)
        {
            _source = source;
            _view = view;
        }

        public nint Handle => _source.Handle;
        public System.Windows.Size Measure() => _view.FitContent();

        public void Show()
        {
            if (_disposed)
            {
                return;
            }

            _ = TaskbarNativeMethods.ShowWindow(Handle, TaskbarNativeMethods.ShowNoActivate);
            RaiseAndRedraw();
        }

        public void Hide()
        {
            if (!_disposed)
            {
                _ = TaskbarNativeMethods.ShowWindow(Handle, TaskbarNativeMethods.HideWindow);
            }
        }

        public void RaiseAndRedraw()
        {
            if (_disposed)
            {
                return;
            }

            _view.InvalidateVisual();

            // Deliberately no SWP_SHOWWINDOW: a repaint must never change visibility.
            _ = TaskbarNativeMethods.SetWindowPos(
                Handle,
                nint.Zero,
                0,
                0,
                0,
                0,
                TaskbarNativeMethods.NoMove
                    | TaskbarNativeMethods.NoSize
                    | TaskbarNativeMethods.NoActivate
                    | TaskbarNativeMethods.FrameChanged);

            _ = TaskbarNativeMethods.RedrawWindow(
                Handle,
                nint.Zero,
                nint.Zero,
                TaskbarNativeMethods.RedrawInvalidate
                    | TaskbarNativeMethods.RedrawFrame
                    | TaskbarNativeMethods.RedrawAllChildren
                    | TaskbarNativeMethods.RedrawUpdateNow);
        }

        public void SetPercentage(string percentage) => _view.SetContent(new
        {
            WeeklyRemainingText = percentage,
            WeeklyRemainingAutomationText = percentage,
            StatusText = "测试数据",
            StatusToolTip = "测试数据",
            StatusKind = Modules.UsageStatus.Live,
        });

        public void SetContent(object dataContext)
        {
            _view.SetContent(dataContext);
            _view.RefreshAction = dataContext is Modules.UsageModule module
                ? () => module.RefreshAsync() : null;
        }

        public bool Move(int x, int y, int width, int height) => !_disposed
            && TaskbarNativeMethods.SetWindowPos(Handle, nint.Zero, x, y, width, height,
                TaskbarNativeMethods.NoActivate | TaskbarNativeMethods.NoZOrder);

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _view.RefreshAction = null;
            _view.SetContent(null);
            try
            {
                _source.Dispose();
            }
            catch
            {
                // Teardown must not throw during probe cleanup.
            }
        }
    }
}
