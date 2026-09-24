using System.Drawing;
using System.Runtime.ExceptionServices;
using System.Windows.Interop;
using CodexQuotaWidget.App;
using CodexQuotaWidget.App.Infrastructure;
using CodexQuotaWidget.App.Modules;
using CodexQuotaWidget.Core;

namespace CodexQuotaWidget.Tests;

[TestClass]
[DoNotParallelize]
public sealed class MainWindowHostLifecycleTests
{
    private const nint TaskbarHandle = 10;
    private const long PopupStyle = unchecked((long)0x80000000L);
    private static readonly nint PopupStyleValue = unchecked((nint)PopupStyle);
    private const long TopmostExtendedStyle = 0x00000008L;

    [TestMethod]
    public void TryExitTaskbarMode_WhenRestoreFails_KeepsHostedStateAndRetainsSnapshot()
    {
        Exception? failure = null;
        var completed = false;
        var thread = new Thread(() =>
        {
            UsageModule? module = null;
            MainWindow? window = null;
            try
            {
                var loggerPath = Path.Combine(Path.GetTempPath(), $"CodexQuotaWidget-lifecycle-{Guid.NewGuid():N}.log");
                var logger = new AppLogger(loggerPath);
                var native = new FakeNative();
                var service = new TaskbarHostService(native, new FakeOccupancy(), logger);
                var settingsService = new SettingsService(
                    logger,
                    Path.Combine(Path.GetTempPath(), $"CodexQuotaWidget-lifecycle-{Guid.NewGuid():N}.json"));
                var placement = new WindowPlacementService(settingsService, logger, persistSettings: false);
                module = new UsageModule(new QuotaRefreshCoordinator(new NoOpQuotaProvider()));

                window = new MainWindow(
                    module,
                    placement,
                    new AppSettings { HostMode = WindowHostMode.Taskbar },
                    service,
                    settingsService: null);
                var hwnd = new WindowInteropHelper(window).EnsureHandle();

                var attached = window.TryEnterTaskbarMode();
                Assert.IsTrue(
                    attached,
                    $"attached={attached} hwnd={hwnd:x} style={native.GetStyle(hwnd):x} parent={native.GetRealParent(hwnd)} owner={native.GetOwner(hwnd)} pending={service.HasPendingRestoration} hosted={service.IsHosted} width={window.Width} height={window.Height} log={TryReadLog(loggerPath)}");
                Assert.IsTrue(window.IsHosted);
                Assert.IsTrue(window.IsTaskbarManaged);

                // The taskbar parent is cleared, but the style restore fails, leaving a
                // pending restoration that must not be reported as a floating success.
                native.SetStyleFailFromCall = 2;
                Assert.IsFalse(window.TryExitTaskbarMode());
                Assert.AreEqual(WindowHostMode.Taskbar, window.HostMode);
                Assert.IsTrue(window.IsTaskbarManaged);
                Assert.IsTrue(window.HasPendingRestoration);
                Assert.IsFalse(window.IsHosted);

                // The same entry point retries and completes the recovery.
                native.SetStyleFailFromCall = int.MaxValue;
                Assert.IsTrue(window.TryExitTaskbarMode());
                Assert.AreEqual(WindowHostMode.Floating, window.HostMode);
                Assert.IsFalse(window.HasPendingRestoration);
                Assert.IsFalse(window.IsTaskbarManaged);

                window.AllowCloseAndClose();
                completed = true;
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                if (module is not null)
                {
                    module.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(20)), "Host lifecycle test timed out.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        Assert.IsTrue(completed);
    }

    [TestMethod]
    public void HostedHide_UsesNativeVisibilityWithoutLeavingHostedState()
    {
        Exception? failure = null;
        var completed = false;
        var thread = new Thread(() =>
        {
            UsageModule? module = null;
            MainWindow? window = null;
            try
            {
                var logger = new AppLogger(Path.Combine(Path.GetTempPath(), $"CodexQuotaWidget-lifecycle-{Guid.NewGuid():N}.log"));
                var native = new FakeNative();
                var service = new TaskbarHostService(native, new FakeOccupancy(), logger);
                var settingsService = new SettingsService(
                    logger,
                    Path.Combine(Path.GetTempPath(), $"CodexQuotaWidget-lifecycle-{Guid.NewGuid():N}.json"));
                var placement = new WindowPlacementService(settingsService, logger, persistSettings: false);
                module = new UsageModule(new QuotaRefreshCoordinator(new NoOpQuotaProvider()));

                window = new MainWindow(
                    module,
                    placement,
                    new AppSettings { HostMode = WindowHostMode.Taskbar },
                    service,
                    settingsService: null);
                var hwnd = new WindowInteropHelper(window).EnsureHandle();
                Assert.IsTrue(window.TryEnterTaskbarMode());

                // Hosted hide must go through native ShowWindow(false) and keep the host
                // state, so WPF's layered surface is never torn down.
                window.HideToTray();

                Assert.IsFalse(window.IsEffectivelyVisible);
                Assert.IsTrue(window.IsHosted);
                Assert.AreEqual((hwnd, false), native.VisibilityCalls[^1]);

                window.AllowCloseAndClose();
                completed = true;
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                if (module is not null)
                {
                    module.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(20)), "Hosted hide test timed out.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        Assert.IsTrue(completed);
    }

    private static string TryReadLog(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : "<no log>";
        }
        catch (Exception ex)
        {
            return ex.GetType().Name;
        }
    }

    private sealed class FakeOccupancy : ITaskbarOccupancyProvider
    {
        public bool TryGetOccupiedRegions(
            nint taskbarHandle,
            nint excludeHandle,
            out IReadOnlyList<Rectangle> occupiedRegions)
        {
            occupiedRegions =
            [
                new Rectangle(0, 0, 200, 48),
                new Rectangle(702, 0, 890, 48),
                new Rectangle(1872, 0, 48, 48),
            ];
            return true;
        }
    }

    private sealed class FakeNative : ITaskbarHostNative
    {
        private readonly Dictionary<nint, nint> _parents = new();
        private readonly Dictionary<nint, TaskbarRect> _rects = new();
        private int _setStyleCalls;

        public nint TaskbarHandle { get; set; } = MainWindowHostLifecycleTests.TaskbarHandle;
        public bool SetParentFails { get; set; }
        public bool SetOwnerFails { get; set; }
        public int SetStyleFailFromCall { get; set; } = int.MaxValue;
        public bool MoveWindowFails { get; set; }
        public uint Dpi { get; set; } = 96;
        public TaskbarRect TaskbarRect { get; set; } = new() { Left = 0, Top = 0, Right = 1920, Bottom = 48 };
        public Dictionary<nint, nint> Styles { get; } = new();
        public Dictionary<nint, nint> ExtendedStyles { get; } = new();
        public Dictionary<nint, nint> Owners { get; } = new();
        public List<(nint Handle, bool Visible)> VisibilityCalls { get; } = new();
        public int RaiseAndRedrawCalls { get; private set; }

        public nint FindTaskbar() => TaskbarHandle;

        public nint GetRealParent(nint windowHandle) =>
            _parents.TryGetValue(windowHandle, out var parent) ? parent : nint.Zero;

        public nint GetDesktopHandle() => 1;

        public nint SetParent(nint childHandle, nint parentHandle)
        {
            var previous = GetRealParent(childHandle);
            if (!SetParentFails)
            {
                _parents[childHandle] = parentHandle;
            }

            return previous;
        }

        public bool IsWindow(nint windowHandle) => windowHandle != nint.Zero;

        public bool GetWindowRect(nint windowHandle, out TaskbarRect rectangle)
        {
            if (windowHandle == TaskbarHandle)
            {
                rectangle = TaskbarRect;
                return true;
            }

            if (_rects.TryGetValue(windowHandle, out rectangle))
            {
                return true;
            }

            rectangle = default;
            return false;
        }

        public uint GetDpiForWindow(nint windowHandle) => Dpi;

        public bool MoveWindow(nint windowHandle, int x, int y, int width, int height)
        {
            if (MoveWindowFails)
            {
                return false;
            }

            _rects[windowHandle] = new TaskbarRect { Left = x, Top = y, Right = x + width, Bottom = y + height };
            return true;
        }

        public bool RefreshWindowFrame(nint windowHandle) => true;

        public bool SetWindowVisible(nint windowHandle, bool visible)
        {
            VisibilityCalls.Add((windowHandle, visible));
            return true;
        }

        public bool IsWindowVisible(nint windowHandle) => true;

        public bool RaiseAndRedrawWindow(nint windowHandle)
        {
            RaiseAndRedrawCalls++;
            return true;
        }

        public void ScreenToClient(nint windowHandle, ref TaskbarPoint point)
        {
        }

        public nint GetStyle(nint windowHandle) =>
            Styles.TryGetValue(windowHandle, out var style)
                ? style
                : windowHandle == TaskbarHandle ? nint.Zero : PopupStyleValue;

        public nint GetExtendedStyle(nint windowHandle) =>
            ExtendedStyles.TryGetValue(windowHandle, out var style)
                ? style
                : windowHandle == TaskbarHandle ? nint.Zero : (nint)TopmostExtendedStyle;

        public nint SetStyle(nint windowHandle, nint style)
        {
            var previous = GetStyle(windowHandle);
            _setStyleCalls++;
            if (_setStyleCalls < SetStyleFailFromCall)
            {
                Styles[windowHandle] = style;
            }

            return previous;
        }

        public nint SetExtendedStyle(nint windowHandle, nint extendedStyle)
        {
            var previous = GetExtendedStyle(windowHandle);
            ExtendedStyles[windowHandle] = extendedStyle;
            return previous;
        }

        public nint GetOwner(nint windowHandle) =>
            Owners.TryGetValue(windowHandle, out var owner) ? owner : nint.Zero;

        public nint SetOwner(nint windowHandle, nint ownerHandle)
        {
            var previous = GetOwner(windowHandle);
            if (!SetOwnerFails)
            {
                Owners[windowHandle] = ownerHandle;
            }

            return previous;
        }

        public int LastWin32Error => 0;
    }

    private sealed class NoOpQuotaProvider : IQuotaProvider
    {
#pragma warning disable CS0067
        public event EventHandler? QuotaChanged;
#pragma warning restore CS0067

        public Task<QuotaSnapshot> GetQuotaAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The lifecycle-test provider must not be queried.");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}