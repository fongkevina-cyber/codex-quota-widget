using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Windows;
using CodexQuotaWidget.App.Infrastructure;
using CodexQuotaWidget.App.Modules;
using CodexQuotaWidget.Core;
using Microsoft.Win32;

namespace CodexQuotaWidget.App;

public partial class App : System.Windows.Application
{
    private readonly SingleInstanceService _singleInstance = new();
    private readonly AppLogger _logger = new();
    private ThemeService? _themeService;
    private SettingsService? _settingsService;
    private StartupShortcutService? _startupShortcutService;
    private WidgetPresentationCoordinator? _presentation;
    private System.Windows.Threading.DispatcherTimer? _presentationTimer;
    private bool _changingPresentation;
    private UsageModule? _usageModule;
    private MainWindow? _mainWindow;
    private TrayIconService? _trayIcon;
    private IdleMemoryTrimmer? _memoryTrimmer;
    private bool _isExiting;
    private bool _resourcesDisposed;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var arguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (arguments.Contains("--taskbar-widget-test", StringComparer.OrdinalIgnoreCase))
        {
            Environment.Exit(await RunTaskbarWidgetTestAsync());
            return;
        }
        if (arguments.Any(argument =>
                string.Equals(argument, "--smoke-test", StringComparison.OrdinalIgnoreCase)))
        {
            Environment.Exit(await RunSmokeTestAsync());
            return;
        }

        if (arguments.Any(argument =>
                string.Equals(argument, "--taskbar-host-test", StringComparison.OrdinalIgnoreCase)))
        {
            Environment.Exit(await RunTaskbarHostTestAsync());
            return;
        }

        if (arguments.Any(argument =>
                string.Equals(argument, "--taskbar-host-probe", StringComparison.OrdinalIgnoreCase)))
        {
            Environment.Exit(await RunTaskbarHostProbeAsync());
            return;
        }


        if (!_singleInstance.TryAcquire())
        {
            Shutdown(0);
            return;
        }

        _singleInstance.ActivationRequested += OnActivationRequested;

        try
        {
            _themeService = new ThemeService(_logger);
            _themeService.Start();

            _settingsService = new SettingsService(_logger);
            var settings = _settingsService.Load();
            if (arguments.Contains("--taskbar", StringComparer.OrdinalIgnoreCase))
            {
                settings.HostMode = WindowHostMode.Taskbar;
                _settingsService.SaveCurrent();
            }
            _startupShortcutService = new StartupShortcutService(_logger);
            ReconcileStartupShortcut(settings);
            _memoryTrimmer = new IdleMemoryTrimmer(Dispatcher, _logger);

            var codexExecutable = Path.Combine(AppContext.BaseDirectory, "runtime", "codex.exe");
            var provider = new CodexAppServerQuotaProvider(new CodexAppServerOptions(codexExecutable)
            {
                TrimAppServerWorkingSet = true,
            });
            var coordinator = new QuotaRefreshCoordinator(provider);
            _usageModule = new UsageModule(coordinator);
            _usageModule.PropertyChanged += OnUsageModulePropertyChanged;

            var placement = new WindowPlacementService(_settingsService, _logger);
            // The desktop window is always floating. Only the independent HwndSource
            // is a taskbar child; production never reparents a transparent WPF window.
            _mainWindow = new MainWindow(_usageModule, placement,
                new AppSettings { HostMode = WindowHostMode.Floating, Topmost = settings.Topmost })
            {
                Topmost = settings.Topmost
            };
            _mainWindow.VisibilityChangedByUser += OnWindowVisibilityChanged;
            _presentation = new WidgetPresentationCoordinator(settings.HostMode, _usageModule,
                () => new TaskbarCapsuleHost(new TaskbarCapsuleEnvironment(),
                    new HwndSourceCapsuleSurfaceFactory(), _logger),
                SetFloatingVisible,
                mode => { settings.HostMode = mode; _settingsService.SaveCurrent(); }, _logger);
            _presentation.Changed += OnPresentationChanged;
            _presentation.Reconcile();
            _presentationTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(20),
            };
            _presentationTimer.Tick += (_, _) =>
            {
                if (_presentation?.Mode == WindowHostMode.Taskbar) _presentation.Reconcile();
            };
            _presentationTimer.Start();

            _trayIcon = new TrayIconService(
                toggleWindow: ToggleWindowVisibility,
                toggleTopmost: ToggleTopmost,
                refresh: RefreshNowAsync,
                setStartup: SetStartupEnabled,
                exit: RequestExitAsync,
                isWindowVisible: () => _presentation?.WantsVisible == true,
                isTopmost: () => _mainWindow?.Topmost == true,
                isStartupEnabled: () => _startupShortcutService?.IsEnabled == true,
                logger: _logger,
                toggleTaskbarHost: ToggleTaskbarHost,
                isTaskbarHosted: () => _presentation?.Mode == WindowHostMode.Taskbar);
            _trayIcon.TaskbarRecreated += OnTaskbarRecreated;

            SubscribeSystemRefreshEvents();
            await _usageModule.StartAsync();
        }
        catch (OperationCanceledException) when (_isExiting || _resourcesDisposed)
        {
            // A tray exit or Windows shutdown can cancel the initial app-server read.
            // That is a normal lifecycle transition, not a startup failure.
        }
        catch (Exception ex)
        {
            _logger.Write("startup_failed", ex);
            System.Windows.MessageBox.Show(
                "Codex 额度组件暂时无法启动。请重新打开；如果问题持续，请查看本地日志。",
                "Codex 额度",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            await DisposeResourcesAsync();
            Shutdown(1);
        }
    }


    private async Task<int> RunSmokeTestAsync()
    {
        UsageModule? module = null;
        try
        {
            string[] requiredResources =
            [
                "WindowBackgroundBrush", "TextPrimaryBrush", "DataAccentBrush", "StatusLiveBrush"
            ];
            if (!requiredResources.All(Resources.Contains))
            {
                return 2;
            }

            var coordinator = new QuotaRefreshCoordinator(new SmokeQuotaProvider());
            module = new UsageModule(coordinator);
            var settingsService = new SettingsService(_logger);
            var placement = new WindowPlacementService(settingsService, _logger);
            _ = new MainWindow(module, placement, new AppSettings());
            return 0;
        }
        catch
        {
            return 2;
        }
        finally
        {
            if (module is not null)
            {
                await module.DisposeAsync();
            }
        }
    }

    private sealed class SmokeQuotaProvider : IQuotaProvider
    {
        public event EventHandler? QuotaChanged
        {
            add { }
            remove { }
        }

        public Task<QuotaSnapshot> GetQuotaAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The smoke-test provider must not be queried.");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Isolated acceptance entry: attaches the real capsule window to the taskbar with a
    /// fixed test percentage, runs a short detach/re-attach phase, keeps it visible long
    /// enough for the operator to inspect the taskbar, then detaches and exits. It never
    /// reads real quota, never touches user settings, and never restarts Explorer. The
    /// reported result is only the real parent/WS_CHILD relationship, not visibility.
    /// </summary>
    private async Task<int> RunTaskbarHostTestAsync()
    {
        const int testPercentage = 88;
        var reportLogger = new AppLogger(Path.Combine(
            Path.GetTempPath(),
            "CodexQuotaWidget-taskbar-host-test.log"));

        UsageModule? module = null;
        MainWindow? window = null;
        try
        {
            var coordinator = new QuotaRefreshCoordinator(new FixedQuotaProvider(testPercentage));
            module = new UsageModule(coordinator);
            await module.StartAsync();

            // persistSettings:false keeps the verification from rewriting user settings.
            var settingsService = new SettingsService(
                reportLogger,
                Path.Combine(Path.GetTempPath(), "CodexQuotaWidget-taskbar-host-test-settings.json"));
            var placement = new WindowPlacementService(settingsService, reportLogger, persistSettings: false);
            var hostService = new TaskbarHostService(reportLogger);
            var settings = new AppSettings { HostMode = WindowHostMode.Taskbar };

            window = new MainWindow(module, placement, settings, hostService, settingsService: null)
            {
                Title = $"Codex 额度任务栏挂靠验证（测试百分比 {testPercentage}%）",
            };
            System.Windows.Automation.AutomationProperties.SetName(
                window,
                $"任务栏挂靠验证 测试百分比 {testPercentage}%");

            // Create the HWND and attach before showing so a host failure never presents a
            // floating capsule as an attachment demo.
            _ = new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
            if (!window.TryEnterTaskbarMode())
            {
                reportLogger.Write("taskbar_host_test_phase_attach_failed");
                window.Hide();
                TryWriteTestSummary(false, testPercentage);
                return 3;
            }

            window.Show();
            reportLogger.Write("taskbar_host_test_phase_attached");
            await Task.Delay(TimeSpan.FromSeconds(20));

            var detachOk = window.TryExitTaskbarMode();
            var reattachOk = false;
            if (detachOk)
            {
                // Floating hide uses the normal tray hide path.
                window.HideToTray();
                reportLogger.Write("taskbar_host_test_phase_detached");
                await Task.Delay(TimeSpan.FromSeconds(2));

                reattachOk = window.TryEnterTaskbarMode();
                if (reattachOk)
                {
                    // Hosted show goes through the production tray show path, which repaints.
                    window.ShowFromTray();
                    reportLogger.Write("taskbar_host_test_phase_reattached");
                    await Task.Delay(TimeSpan.FromSeconds(23));
                }
                else
                {
                    reportLogger.Write("taskbar_host_test_phase_reattach_failed");
                    window.HideToTray();
                }
            }
            else
            {
                reportLogger.Write("taskbar_host_test_phase_detach_failed");
                await Task.Delay(TimeSpan.FromSeconds(25));
            }

            // Structural success requires the real taskbar parent and WS_CHILD, not just
            // "still related to the taskbar".
            var hosted = window.IsHosted;
            reportLogger.Write(hosted
                ? "taskbar_host_test_parent_is_taskbar"
                : "taskbar_host_test_parent_not_taskbar");
            reportLogger.Write(window.IsHostHandleAlive
                ? "taskbar_host_test_handle_alive"
                : "taskbar_host_test_handle_destroyed");

            // Final cleanup detach is part of the pass criteria, so a failed detach can
            // never be reported as success.
            var cleanupDetachOk = window.TryExitTaskbarMode();
            if (!cleanupDetachOk)
            {
                reportLogger.Write("taskbar_host_test_cleanup_detach_failed");
            }

            window.Hide();

            var success = detachOk && reattachOk && hosted && cleanupDetachOk;
            reportLogger.Write(success
                ? "taskbar_host_test_structural_pass"
                : "taskbar_host_test_structural_fail");
            TryWriteTestSummary(success, testPercentage);
            return success ? 0 : 3;
        }
        catch (Exception ex)
        {
            reportLogger.Write("taskbar_host_test_error", ex);
            return 3;
        }
        finally
        {
            try
            {
                if (window is not null)
                {
                    window.TryExitTaskbarMode();
                    window.AllowCloseAndClose();
                }
            }
            catch (Exception ex)
            {
                reportLogger.Write("taskbar_host_test_cleanup_failed", ex);
            }

            if (module is not null)
            {
                await module.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Isolated taskbar-host experiment: creates a dedicated HwndSource whose parent is
    /// Shell_TrayWnd from creation, then runs a fixed 45s hide/show/recreate timeline with
    /// MOCK percentages. It never reads quota, never touches settings or the installed
    /// widget, and never restarts Explorer.
    /// </summary>
    private async Task<int> RunTaskbarHostProbeAsync()
    {
        var logger = new AppLogger(Path.Combine(
            Path.GetTempPath(),
            "CodexQuotaWidget-taskbar-host-probe.log"));
        var stopwatch = Stopwatch.StartNew();
        var environment = new TaskbarCapsuleEnvironment();
        var factory = new HwndSourceCapsuleSurfaceFactory();
        ThemeService? theme = null;
        TaskbarCapsuleHost? host = null;

        void LogGeometry(string phase)
        {
            if (host is null || !host.TryGetProbeGeometry(out var hwnd, out var parent, out var bounds))
            {
                logger.Write($"probe_{phase}_geometry_unavailable");
                return;
            }

            logger.Write(
                $"probe_{phase}_ms{stopwatch.ElapsedMilliseconds}_hwnd{unchecked((ulong)hwnd):x}_parent{unchecked((ulong)parent):x}");
            logger.Write(
                $"probe_{phase}_rect{unchecked((ulong)(long)bounds.Left):x}_{unchecked((ulong)(long)bounds.Top):x}_{unchecked((ulong)(long)bounds.Right):x}_{unchecked((ulong)(long)bounds.Bottom):x}_mode{host.TransparencyMode}");
        }

        try
        {
            // Follow the same theme resources as the production capsule.
            theme = new ThemeService(logger);
            theme.Start();

            host = new TaskbarCapsuleHost(environment, factory, logger);
            if (!host.TryCreate("88%", out var createReason))
            {
                logger.Write($"probe_create_failed_{createReason}");
                // No safe slot is "not acceptable to verify"; other failures are hard.
                return string.Equals(createReason, "no_safe_slot", StringComparison.Ordinal) ? 2 : 3;
            }

            LogGeometry("create");
            host.Show();
            host.SetPercentage("88%");
            LogGeometry("phase_attached_88");
            await Task.Delay(TimeSpan.FromSeconds(12));

            host.Hide();
            LogGeometry("phase_hidden");
            await Task.Delay(TimeSpan.FromSeconds(2));

            host.Show();
            host.SetPercentage("77%");
            LogGeometry("phase_shown_77");
            await Task.Delay(TimeSpan.FromSeconds(12));

            var firstHandle = host.HostHandle;
            host.Destroy();
            host = null;
            logger.Write($"probe_destroy_ms{stopwatch.ElapsedMilliseconds}");

            if (firstHandle != nint.Zero && TaskbarNativeMethods.IsWindow(firstHandle))
            {
                logger.Write("probe_destroy_handle_still_valid");
                return 3;
            }

            logger.Write("probe_destroy_handle_released");
            await Task.Delay(TimeSpan.FromSeconds(2));

            host = new TaskbarCapsuleHost(environment, factory, logger);
            if (!host.TryCreate("66%", out var recreateReason))
            {
                logger.Write($"probe_recreate_failed_{recreateReason}");
                return string.Equals(recreateReason, "no_safe_slot", StringComparison.Ordinal) ? 2 : 3;
            }

            LogGeometry("recreate");
            host.Show();
            host.SetPercentage("66%");
            LogGeometry("phase_recreated_66");
            await Task.Delay(TimeSpan.FromSeconds(17));

            host.Destroy();
            host = null;
            logger.Write($"probe_cleanup_ms{stopwatch.ElapsedMilliseconds}");
            return 0;
        }
        catch (Exception ex)
        {
            logger.Write("probe_error", ex);
            return 3;
        }
        finally
        {
            try
            {
                host?.Destroy();
            }
            catch (Exception ex)
            {
                logger.Write("probe_cleanup_failed", ex);
            }

            try
            {
                theme?.Dispose();
            }
            catch (Exception ex)
            {
                logger.Write("probe_theme_dispose_failed", ex);
            }
        }
    }

    private static void TryWriteTestSummary(bool success, int testPercentage)
    {
        try
        {
            Console.Error.WriteLine(
                $"taskbar-host-test success={success} percentage={testPercentage}");
        }
        catch
        {
            // WinExe processes may not have a console; the log entry above is authoritative.
        }
    }

    private sealed class FixedQuotaProvider : IQuotaProvider
    {
        public int RemainingPercent { get; set; }

        public FixedQuotaProvider(int remainingPercent)
        {
            RemainingPercent = remainingPercent;
        }

        public event EventHandler? QuotaChanged
        {
            add { }
            remove { }
        }

        public Task<QuotaSnapshot> GetQuotaAsync(CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.Now;
            var weekly = new QuotaWindow(
                WindowDurationMinutes: 10_080,
                UsedPercent: 100m - RemainingPercent,
                ResetsAt: now.AddDays(3));
            return Task.FromResult(new QuotaSnapshot(
                FiveHour: null,
                Weekly: weekly,
                AvailableResetCount: null,
                RetrievedAt: now));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private void ReconcileStartupShortcut(AppSettings settings)
    {
        if (_startupShortcutService is null || _settingsService is null)
        {
            return;
        }

        try
        {
            _startupShortcutService.SetEnabled(settings.AutoStart);
        }
        catch (Exception ex)
        {
            _logger.Write("startup_shortcut_reconcile_failed", ex);
            settings.AutoStart = _startupShortcutService.IsEnabled;
            _settingsService.Save(settings);
        }
    }

    private void SubscribeSystemRefreshEvents()
    {
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
    }

    private void UnsubscribeSystemRefreshEvents()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_presentation?.Mode == WindowHostMode.Floating && _mainWindow?.IsVisible == true)
                _mainWindow.ConstrainToVisibleArea();
            _presentation?.Reconcile();
        });
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            Dispatcher.BeginInvoke(() =>
            {
                _presentation?.Reconcile();
                _usageModule?.NotifyConnectivityRestored();
            });
        }
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable)
        {
            Dispatcher.BeginInvoke(() => _usageModule?.NotifyConnectivityRestored());
        }
    }

    private void OnActivationRequested(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(ShowWindow);
    }

    private void SetFloatingVisible(bool visible)
    {
        if (_mainWindow is null) return;
        _changingPresentation = true;
        try
        {
            if (visible && !_mainWindow.IsVisible) _mainWindow.ShowFromTray();
            else if (!visible && _mainWindow.IsVisible) _mainWindow.HideToTray();
        }
        finally { _changingPresentation = false; }
    }

    private void OnWindowVisibilityChanged(object? sender, EventArgs e)
    {
        if (!_changingPresentation && _presentation?.Mode == WindowHostMode.Floating)
            _presentation.SetVisible(_mainWindow?.IsVisible == true);
    }

    private void OnPresentationChanged(object? sender, EventArgs e)
    {
        _usageModule?.SetVisible(_presentation?.IsVisible == true);
        _trayIcon?.RefreshMenuState();
    }

    private void OnTaskbarRecreated(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() => _presentation?.RecreateHost());

    private void OnUsageModulePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName))
        {
            _memoryTrimmer?.Schedule();
        }
    }

    private void ToggleWindowVisibility() =>
        _presentation?.SetVisible(!_presentation.WantsVisible);

    private void ShowWindow() => _presentation?.SetVisible(true);

    private bool ToggleTopmost()
    {
        if (_mainWindow is null || _settingsService is null)
        {
            return false;
        }

        _mainWindow.Topmost = !_mainWindow.Topmost;
        _settingsService.Current.Topmost = _mainWindow.Topmost;
        _settingsService.SaveCurrent();
        return _mainWindow.Topmost;
    }

    private bool ToggleTaskbarHost()
    {
        if (_presentation is null) return false;
        _presentation.SetMode(_presentation.Mode == WindowHostMode.Taskbar
            ? WindowHostMode.Floating : WindowHostMode.Taskbar);
        return _presentation.Mode == WindowHostMode.Taskbar;
    }

    private async Task RefreshNowAsync()
    {
        if (_usageModule is null)
        {
            return;
        }

        try
        {
            await _usageModule.RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.Write("manual_refresh_failed", ex);
        }
    }

    private bool SetStartupEnabled(bool enabled)
    {
        if (_startupShortcutService is null || _settingsService is null)
        {
            return false;
        }

        try
        {
            _startupShortcutService.SetEnabled(enabled);
            _settingsService.Current.AutoStart = enabled;
            _settingsService.SaveCurrent();
            return enabled;
        }
        catch (Exception ex)
        {
            _logger.Write("startup_shortcut_change_failed", ex);
            return _startupShortcutService.IsEnabled;
        }
    }

    private async Task RequestExitAsync()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        await DisposeResourcesAsync();
        Shutdown(0);
    }

    private async Task DisposeResourcesAsync()
    {
        if (_resourcesDisposed)
        {
            return;
        }

        _resourcesDisposed = true;
        _presentationTimer?.Stop();
        _presentation?.Dispose();
        if (_mainWindow is not null)
        {
            _mainWindow.VisibilityChangedByUser -= OnWindowVisibilityChanged;
            _mainWindow.AllowCloseAndClose(preservePlacement: !_mainWindow.IsVisible);
        }
        UnsubscribeSystemRefreshEvents();
        _singleInstance.ActivationRequested -= OnActivationRequested;
        if (_trayIcon is not null)
        {
            _trayIcon.TaskbarRecreated -= OnTaskbarRecreated;
            _trayIcon.Dispose();
        }

        _themeService?.Dispose();

        if (_usageModule is not null)
        {
            _usageModule.PropertyChanged -= OnUsageModulePropertyChanged;
            try
            {
                await _usageModule.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Write("quota_shutdown_failed", ex);
            }
        }

        _memoryTrimmer?.Dispose();
        _singleInstance.Dispose();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!_resourcesDisposed)
        {
            DisposeResourcesAsync().GetAwaiter().GetResult();
        }

        base.OnExit(e);
    }
}
