namespace CodexQuotaWidget.App.Infrastructure;

/// <summary>
/// Owns presentation only. A taskbar child and a floating WPF window never exchange
/// HWNDs; both bind to the same module. User intent survives temporary layout failures.
/// All calls run on the UI dispatcher, including the low-frequency layout timer.
/// </summary>
internal sealed class WidgetPresentationCoordinator : IDisposable
{
    private readonly Func<TaskbarCapsuleHost> _createHost;
    private readonly object _content;
    private readonly Action<bool> _setFloatingVisible;
    private readonly Action<WindowHostMode> _saveMode;
    private readonly AppLogger _logger;
    private TaskbarCapsuleHost? _host;
    private bool _disposed;

    public WidgetPresentationCoordinator(WindowHostMode mode, object content,
        Func<TaskbarCapsuleHost> createHost, Action<bool> setFloatingVisible,
        Action<WindowHostMode> saveMode, AppLogger logger)
    {
        Mode = mode;
        _content = content;
        _createHost = createHost;
        _setFloatingVisible = setFloatingVisible;
        _saveMode = saveMode;
        _logger = logger;
    }

    public WindowHostMode Mode { get; private set; }
    public bool WantsVisible { get; private set; } = true;
    public bool IsVisible { get; private set; }
    public nint HostHandle => _host?.HostHandle ?? nint.Zero;
    public event EventHandler? Changed;

    public void SetVisible(bool visible)
    {
        if (_disposed) return;
        WantsVisible = visible;
        Reconcile();
    }

    public void SetMode(WindowHostMode mode)
    {
        if (_disposed || mode == Mode) return;
        // Hide the old presentation before committing the new mode, never both visible.
        _setFloatingVisible(false);
        _host?.Dispose();
        _host = null;
        Mode = mode;
        _saveMode(mode);
        Reconcile();
    }

    public void RecreateHost()
    {
        if (_disposed || Mode != WindowHostMode.Taskbar) return;
        _host?.Dispose();
        _host = null;
        Reconcile();
    }

    public void Reconcile()
    {
        if (_disposed) return;
        try
        {
            if (Mode == WindowHostMode.Floating)
            {
                _setFloatingVisible(WantsVisible);
                IsVisible = WantsVisible;
            }
            else
            {
                _setFloatingVisible(false);
                if (_host is not null && !_host.IsAlive)
                {
                    _host.Dispose();
                    _host = null;
                }
                if (!WantsVisible)
                {
                    _host?.Hide();
                    IsVisible = false;
                }
                else
                {
                    if (_host is null)
                    {
                        var candidate = _createHost();
                        if (candidate.TryCreate("—", out _))
                        {
                            candidate.SetContent(_content);
                            _host = candidate;
                        }
                        else candidate.Dispose();
                    }
                    IsVisible = _host is not null && (_host.IsHidden
                        ? _host.Show() : _host.TryReposition());
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Write("presentation_reconcile_failed", ex);
            _host?.Dispose();
            _host = null;
            IsVisible = false;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _host?.Dispose();
        _host = null;
        IsVisible = false;
        Changed = null;
    }
}
