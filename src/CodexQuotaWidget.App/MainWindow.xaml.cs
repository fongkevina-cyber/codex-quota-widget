using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CodexQuotaWidget.App.Infrastructure;
using CodexQuotaWidget.App.Modules;

namespace CodexQuotaWidget.App;

public partial class MainWindow : Window
{
    private static readonly TimeSpan LayoutVerificationInterval = TimeSpan.FromSeconds(20);

    private readonly WindowPlacementService _placementService;
    private readonly TaskbarHostService? _hostService;
    private readonly SettingsService? _settingsService;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer? _layoutVerificationTimer;
    private WindowHostMode _hostMode;
    private bool _allowClose;
    private bool _suppressPlacementSave;
    private bool _hiddenForLayout;
    private bool _hostedHidden;

    public MainWindow(
        UsageModule usageModule,
        WindowPlacementService placementService,
        AppSettings settings,
        TaskbarHostService? hostService = null,
        SettingsService? settingsService = null)
    {
        ArgumentNullException.ThrowIfNull(usageModule);
        ArgumentNullException.ThrowIfNull(placementService);
        ArgumentNullException.ThrowIfNull(settings);

        _placementService = placementService;
        _hostService = hostService;
        _settingsService = settingsService;
        _settings = settings;
        _hostMode = settings.HostMode;
        DataContext = usageModule;
        InitializeComponent();
        Width = Readout.Width;
        Height = Readout.Height;

        if (_hostService is not null)
        {
            _layoutVerificationTimer = new DispatcherTimer(
                DispatcherPriority.Background,
                Dispatcher)
            {
                Interval = LayoutVerificationInterval,
            };
            _layoutVerificationTimer.Tick += OnLayoutVerificationTick;
        }

        SourceInitialized += (_, _) => AltTabVisibilityService.Exclude(this);
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
        DpiChanged += OnDpiChanged;
        IsVisibleChanged += (_, _) => VisibilityChangedByUser?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? VisibilityChangedByUser;

    public event EventHandler? HostModeChanged;

    public WindowHostMode HostMode => _hostMode;

    /// <summary>
    /// Reports a healthy hosted state (taskbar parent plus WS_CHILD), not just the
    /// persisted preference.
    /// </summary>
    public bool IsHosted => _hostService is null
        ? _hostMode == WindowHostMode.Taskbar
        : _hostService.IsHosted;

    /// <summary>
    /// True while the taskbar still owns the window, even after a partial detach. Such a
    /// window must not be handed to the floating workspace constraint.
    /// </summary>
    public bool IsTaskbarOwned => _hostService is null
        ? _hostMode == WindowHostMode.Taskbar
        : _hostService.IsTaskbarOwned;

    /// <summary>
    /// True when the host has an original snapshot that is not fully restored yet. The
    /// window must keep trying to recover and must not be positioned/saved as floating.
    /// </summary>
    public bool HasPendingRestoration => _hostService?.HasPendingRestoration ?? false;

    /// <summary>
    /// True while the taskbar owns the window or a restore is still outstanding.
    /// </summary>
    public bool IsTaskbarManaged => _hostService is null
        ? _hostMode == WindowHostMode.Taskbar
        : _hostService.IsTaskbarManaged;

    public bool IsHostHandleAlive => _hostService?.IsHostHandleAlive ?? true;

    /// <summary>
    /// Visibility as the user sees it. A hosted window stays WPF-visible while it is hidden
    /// natively, so tray and refresh logic must use this instead of <see cref="Window.IsVisible"/>.
    /// </summary>
    public bool IsEffectivelyVisible => IsVisible && !_hostedHidden;

    /// <summary>
    /// Forces the WPF content and the native layered surface to repaint after a reparent or
    /// show. Bounded to one render-priority pass, never a loop.
    /// </summary>
    private void RefreshHostedRendering()
    {
        if (!IsTaskbarOwned)
        {
            return;
        }

        InvalidateVisual();
        if (Content is FrameworkElement root)
        {
            root.InvalidateMeasure();
            root.InvalidateArrange();
            root.InvalidateVisual();
        }

        UpdateLayout();
        _hostService?.RefreshHosted(this);

        Dispatcher.BeginInvoke(
            () =>
            {
                if (!IsTaskbarOwned)
                {
                    return;
                }

                if (Content is FrameworkElement lateRoot)
                {
                    lateRoot.InvalidateVisual();
                }

                InvalidateVisual();
                _hostService?.RefreshHosted(this);
            },
            DispatcherPriority.Render);
    }

    public bool TryEnterTaskbarMode()
    {
        if (IsHosted)
        {
            return true;
        }

        if (_hostService is null || !_hostService.TryAttach(this))
        {
            // A failed attach whose rollback also failed leaves a pending restoration; keep
            // the taskbar mode so the same entry point retries instead of pretending to be
            // a clean floating window.
            if (HasPendingRestoration)
            {
                _hostMode = WindowHostMode.Taskbar;
                HostModeChanged?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                _hostMode = WindowHostMode.Floating;
            }

            return false;
        }

        _hostMode = WindowHostMode.Taskbar;
        // A topmost child is meaningless and can fight the taskbar's own z-order.
        Topmost = false;
        _hiddenForLayout = false;
        _hostedHidden = false;
        _settings.HostMode = WindowHostMode.Taskbar;
        _settingsService?.SaveCurrent();
        _layoutVerificationTimer?.Start();
        HostModeChanged?.Invoke(this, EventArgs.Empty);
        RefreshHostedRendering();
        return true;
    }

    public bool TryExitTaskbarMode()
    {
        if (_hostService is null)
        {
            _hostMode = WindowHostMode.Floating;
            return true;
        }

        if (!_hostService.IsTaskbarManaged)
        {
            _hostMode = WindowHostMode.Floating;
            return true;
        }

        if (!_hostService.TryDetach(this))
        {
            // Still owned or only partly restored: keep the taskbar mode and the retained
            // snapshot so a later call can finish the recovery.
            return false;
        }

        _hostMode = WindowHostMode.Floating;
        _layoutVerificationTimer?.Stop();
        _hiddenForLayout = false;
        _hostedHidden = false;
        Topmost = _settings.Topmost;
        _settings.HostMode = WindowHostMode.Floating;
        _settingsService?.SaveCurrent();

        // Restore the floating capsule where the user last kept it.
        _placementService.Restore(this);
        HostModeChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Re-attaches to a replacement taskbar after Explorer restarts. The caller must
    /// recreate the window when <see cref="IsHostHandleAlive"/> is false.
    /// </summary>
    public bool ReattachToTaskbar()
    {
        if (_hostService is null || !_hostService.TryAttach(this))
        {
            return false;
        }

        _hostMode = WindowHostMode.Taskbar;
        Topmost = false;
        _hiddenForLayout = false;
        _hostedHidden = false;
        _layoutVerificationTimer?.Start();
        HostModeChanged?.Invoke(this, EventArgs.Empty);
        RefreshHostedRendering();
        return true;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        if (_hostMode == WindowHostMode.Taskbar)
        {
            if (IsHosted)
            {
                // Already attached before this window was shown (verification entry).
                _layoutVerificationTimer?.Start();
                RefreshHostedRendering();
                return;
            }

            if (_hostService is not null && _hostService.TryAttach(this))
            {
                Topmost = false;
                _layoutVerificationTimer?.Start();
                RefreshHostedRendering();
                return;
            }

            // The host is unavailable: keep the capsule usable as a floating window
            // and remember the downgrade so the next launch does not fight the taskbar.
            _hostMode = WindowHostMode.Floating;
            _settings.HostMode = WindowHostMode.Floating;
            _settingsService?.SaveCurrent();
            HostModeChanged?.Invoke(this, EventArgs.Empty);
        }

        _placementService.Restore(this);
    }

    private void OnLayoutVerificationTick(object? sender, EventArgs e)
    {
        if (!IsTaskbarOwned)
        {
            return;
        }

        ApplyLayoutResult(_hostService?.TryRepositionIfNeeded(this) ?? TaskbarLayoutResult.Failure);
    }

    private void OnDpiChanged(object sender, DpiChangedEventArgs e)
    {
        if (IsTaskbarOwned)
        {
            ApplyLayoutResult(_hostService?.TryReposition(this) ?? TaskbarLayoutResult.Failure);
        }
    }

    private void ApplyLayoutResult(TaskbarLayoutResult result)
    {
        switch (result)
        {
            case TaskbarLayoutResult.NoSafeSlot:
                // Never keep covering newly appeared taskbar controls: hide the capsule
                // but keep the hosted intent and the verification timer so it can return
                // as soon as a gap is available again.
                if (!_hiddenForLayout)
                {
                    _hiddenForLayout = true;
                    HideHostedCapsule();
                }

                break;
            case TaskbarLayoutResult.UpToDate:
            case TaskbarLayoutResult.Repositioned:
                if (_hiddenForLayout)
                {
                    _hiddenForLayout = false;
                    ShowHostedCapsule();
                }

                break;
        }
    }

    /// <summary>Hides the hosted capsule natively, keeping WPF's surface alive.</summary>
    private void HideHostedCapsule()
    {
        if (IsTaskbarOwned)
        {
            _hostedHidden = true;
            _hostService?.HideHosted(this);
            VisibilityChangedByUser?.Invoke(this, EventArgs.Empty);
            return;
        }

        Hide();
    }

    /// <summary>Shows and repaints the hosted capsule, recovering from a native hide.</summary>
    private void ShowHostedCapsule()
    {
        if (!IsTaskbarOwned)
        {
            if (!IsVisible)
            {
                Show();
            }

            return;
        }

        _hostedHidden = false;
        if (!IsVisible)
        {
            Show();
        }

        _hostService?.ShowHosted(this);
        RefreshHostedRendering();
        VisibilityChangedByUser?.Invoke(this, EventArgs.Empty);
    }

    private void DragSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsTaskbarManaged)
        {
            return;
        }

        if (e.ChangedButton != MouseButton.Left || e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            DragMove();
            _placementService.ConstrainAndSave(this);
        }
        catch (InvalidOperationException)
        {
            // DragMove can be interrupted by a display topology or DPI change.
        }
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            HideToTray();
            e.Handled = true;
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            if (!_suppressPlacementSave && !IsTaskbarManaged)
            {
                _placementService.ConstrainAndSave(this);
            }

            return;
        }

        e.Cancel = true;
        HideToTray();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _layoutVerificationTimer?.Stop();
        if (_layoutVerificationTimer is not null)
        {
            _layoutVerificationTimer.Tick -= OnLayoutVerificationTick;
        }
    }

    public void HideToTray()
    {
        if (IsTaskbarOwned)
        {
            // Hosted: hide natively so WPF keeps rendering the layered surface; a later
            // show repaints without losing content.
            _layoutVerificationTimer?.Stop();
            _hostedHidden = true;
            _hostService?.HideHosted(this);
            VisibilityChangedByUser?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (!IsTaskbarManaged && !_suppressPlacementSave)
        {
            _placementService.ConstrainAndSave(this);
        }

        _layoutVerificationTimer?.Stop();
        Hide();
    }

    public void ShowFromTray()
    {
        if (IsTaskbarOwned)
        {
            _hiddenForLayout = false;
            _hostedHidden = false;
            if (!IsVisible)
            {
                Show();
            }

            _hostService?.ShowHosted(this);
            RefreshHostedRendering();
            ApplyLayoutResult(_hostService?.TryReposition(this) ?? TaskbarLayoutResult.Failure);
            _layoutVerificationTimer?.Start();
            VisibilityChangedByUser?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    public void AllowCloseAndClose(bool preservePlacement = false)
    {
        _suppressPlacementSave |= preservePlacement;
        if (IsTaskbarManaged)
        {
            // Detach before teardown so the taskbar keeps no child, and keep the saved
            // floating coordinates untouched.
            _ = _hostService?.TryDetach(this);
            _suppressPlacementSave = true;
        }

        _allowClose = true;
        Close();
    }

    public void ConstrainToVisibleArea()
    {
        if (IsTaskbarOwned)
        {
            ApplyLayoutResult(_hostService?.TryReposition(this) ?? TaskbarLayoutResult.Failure);
            return;
        }

        if (HasPendingRestoration)
        {
            // Do not save a floating position while the original snapshot is unrecovered.
            return;
        }

        _placementService.ConstrainAndSave(this);
    }

    public bool IsHiddenForLayout => _hiddenForLayout;
}
