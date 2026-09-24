using System.Drawing;
using System.Windows.Interop;
using System.Windows.Media;

namespace CodexQuotaWidget.App.Infrastructure;

/// <summary>
/// Native/geometry inputs the capsule host needs. Abstracted so the host's planning and
/// lifecycle logic can be unit tested without a real taskbar or HwndSource.
/// </summary>
internal interface ITaskbarCapsuleEnvironment
{
    nint FindTaskbar();
    bool IsWindow(nint windowHandle);
    bool TryGetBounds(nint windowHandle, out TaskbarRect bounds);
    uint GetDpi(nint taskbarHandle);
    bool TryGetOccupied(nint taskbarHandle, nint excludeHandle, out IReadOnlyList<Rectangle> occupiedRegions);
    TaskbarPoint ToClient(nint taskbarHandle, int x, int y);
    nint GetRealParent(nint windowHandle);
}

/// <summary>A live host surface (win32 window + capsule visual).</summary>
internal interface ITaskbarCapsuleSurface : IDisposable
{
    nint Handle { get; }
    System.Windows.Size Measure();
    void Show();
    void Hide();
    void RaiseAndRedraw();
    void SetPercentage(string percentage);
    void SetContent(object dataContext);
    bool Move(int x, int y, int width, int height);
}

/// <summary>Creates a host surface as a real child of the taskbar from creation.</summary>
internal interface ITaskbarCapsuleSurfaceFactory
{
    System.Windows.Size Measure(string percentage);
    bool TryCreate(
        nint parentHandle,
        int x,
        int y,
        int width,
        int height,
        out ITaskbarCapsuleSurface? surface,
        out string mode,
        out string reason);
}

/// <summary>
/// Minimal, reusable taskbar capsule host. The window is created directly as a WS_CHILD of
/// Shell_TrayWnd (never reparented afterwards), and hide/show/destroy are separate
/// operations guarded by generation so async redraw cannot resurrect a hidden or destroyed
/// window.
/// </summary>
internal sealed class TaskbarCapsuleHost : IDisposable
{
    private readonly ITaskbarCapsuleEnvironment _environment;
    private readonly ITaskbarCapsuleSurfaceFactory _factory;
    private readonly AppLogger _logger;
    private readonly TaskbarHostGuard _guard = new();
    private ITaskbarCapsuleSurface? _surface;
    private nint _taskbarHandle;
    private nint _hostHandle;
    private int _generation;
    private string _transparencyMode = "none";

    public TaskbarCapsuleHost(
        ITaskbarCapsuleEnvironment environment,
        ITaskbarCapsuleSurfaceFactory factory,
        AppLogger logger)
    {
        _environment = environment;
        _factory = factory;
        _logger = logger;
    }

    public bool IsAlive => _guard.IsAlive && _surface is not null
        && _environment.IsWindow(_hostHandle) && _environment.IsWindow(_taskbarHandle)
        && _environment.GetRealParent(_hostHandle) == _taskbarHandle
        && _environment.FindTaskbar() == _taskbarHandle;

    public bool IsHidden => _guard.IsHidden;

    public int Generation => _generation;

    public nint HostHandle => _hostHandle;

    public nint TaskbarHandle => _taskbarHandle;

    public string TransparencyMode => _transparencyMode;

    /// <summary>
    /// Plans a safe slot and creates the host surface. Returns false (without creating
    /// anything) when occupancy is unknown or no gap is wide enough; the capsule stays
    /// hidden rather than covering a taskbar control.
    /// </summary>
    public bool TryCreate(string percentage, out string reason)
    {
        reason = string.Empty;
        if (_guard.IsDisposed)
        {
            reason = "host_disposed";
            return false;
        }

        if (_surface is not null)
        {
            reason = "already_created";
            return false;
        }

        var taskbar = _environment.FindTaskbar();
        if (taskbar == nint.Zero || !_environment.IsWindow(taskbar))
        {
            reason = "taskbar_not_found";
            _logger.Write("capsule_host_taskbar_not_found");
            return false;
        }

        if (!_environment.TryGetBounds(taskbar, out var taskbarBounds))
        {
            reason = "taskbar_bounds_failed";
            _logger.Write("capsule_host_bounds_failed");
            return false;
        }

        var dpi = _environment.GetDpi(taskbar);
        var measured = _factory.Measure(percentage);
        var size = WindowPlacementService.CalculatePhysicalSize(measured.Width, measured.Height, dpi);

        if (!_environment.TryGetOccupied(taskbar, nint.Zero, out var occupied))
        {
            reason = "occupancy_unavailable";
            _logger.Write("capsule_host_occupancy_unavailable");
            return false;
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
                out var screenPosition))
        {
            reason = "no_safe_slot";
            _logger.Write("capsule_host_no_safe_slot");
            return false;
        }

        var client = _environment.ToClient(taskbar, screenPosition.Left, screenPosition.Top);
        if (!_factory.TryCreate(
                taskbar,
                client.X,
                client.Y,
                size.Width,
                size.Height,
                out var surface,
                out var mode,
                out var createReason)
            || surface is null)
        {
            reason = string.IsNullOrEmpty(createReason) ? "surface_create_failed" : createReason;
            _logger.Write("capsule_host_surface_create_failed");
            return false;
        }

        _surface = surface;
        _taskbarHandle = taskbar;
        _hostHandle = surface.Handle;
        _transparencyMode = mode;
        _generation = _guard.StartGeneration();
        _guard.SetHidden(true);
        surface.SetPercentage(percentage);
        _logger.Write($"capsule_host_created_{mode}");
        return true;
    }

    public bool Show()
    {
        if (!IsAlive || !TryReposition())
        {
            return false;
        }

        _surface!.Show();
        _guard.SetHidden(false);
        return true;
    }

    public bool Hide()
    {
        if (_surface is null || !_guard.IsAlive)
        {
            return false;
        }

        _surface.Hide();
        _guard.SetHidden(true);
        return true;
    }

    public void SetPercentage(string percentage)
    {
        if (_surface is not null && _guard.IsAlive)
        {
            _surface.SetPercentage(percentage);
        }
    }

    public void SetContent(object dataContext) => _surface?.SetContent(dataContext);

    public bool TryReposition()
    {
        if (!IsAlive) return false;
        if (!_environment.TryGetBounds(_taskbarHandle, out var bounds)
            || !_environment.TryGetOccupied(_taskbarHandle, _hostHandle, out var occupied))
        {
            Hide();
            return false;
        }
        var measured = _surface!.Measure();
        var size = WindowPlacementService.CalculatePhysicalSize(
            measured.Width, measured.Height, _environment.GetDpi(_taskbarHandle));
        if (!TaskbarLayoutCalculator.TryCalculateHostedPosition(
            Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom),
            occupied, size.Width, size.Height, out var position))
        {
            Hide();
            return false;
        }
        var client = _environment.ToClient(_taskbarHandle, position.Left, position.Top);
        if (_surface!.Move(client.X, client.Y, size.Width, size.Height)) return true;
        Hide();
        return false;
    }

    /// <summary>
    /// Repaints only. Rejected when the generation is stale (destroyed/recreated) or the
    /// capsule is hidden, and it never changes visibility, so it cannot re-show or draw on
    /// a hidden or dead capsule.
    /// </summary>
    internal bool TryRefresh(int generation)
    {
        if (!IsAlive || !_guard.IsCurrent(generation) || _guard.IsHidden)
        {
            return false;
        }

        _surface!.RaiseAndRedraw();
        return true;
    }

    /// <summary>Returns the host HWND, its real parent and its screen rect for diagnostics.</summary>
    internal bool TryGetProbeGeometry(out nint hostHandle, out nint parentHandle, out TaskbarRect bounds)
    {
        hostHandle = _hostHandle;
        parentHandle = hostHandle != nint.Zero ? _environment.GetRealParent(hostHandle) : nint.Zero;
        bounds = default;
        return hostHandle != nint.Zero && _environment.TryGetBounds(hostHandle, out bounds);
    }

    public void Destroy()
    {
        if (_guard.IsDisposed && _surface is null)
        {
            return;
        }

        var surface = _surface;
        _surface = null;
        _hostHandle = nint.Zero;
        _generation = 0;

        try
        {
            surface?.Dispose();
            _logger.Write("capsule_host_destroyed");
        }
        catch (Exception ex)
        {
            _logger.Write("capsule_host_destroy_failed", ex);
        }
        finally
        {
            _guard.Dispose();
        }
    }

    public void Dispose() => Destroy();
}
