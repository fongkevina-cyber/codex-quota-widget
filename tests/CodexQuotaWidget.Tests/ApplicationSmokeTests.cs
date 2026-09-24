using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using CodexQuotaWidget.App;
using CodexQuotaWidget.App.Infrastructure;
using CodexQuotaWidget.App.Modules;
using CodexQuotaWidget.Core;

namespace CodexQuotaWidget.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ApplicationSmokeTests
{
    [TestMethod]
    public void AltTabStyleAddsToolWindowAndRemovesAppWindowWithoutChangingOtherBits()
    {
        const nint unrelatedStyle = 0x00000100;
        const nint appWindowStyle = 0x00040000;
        const nint toolWindowStyle = 0x00000080;

        var result = AltTabVisibilityService.CalculateExcludedStyle(
            unrelatedStyle | appWindowStyle);

        Assert.AreNotEqual(nint.Zero, result & unrelatedStyle);
        Assert.AreNotEqual(nint.Zero, result & toolWindowStyle);
        Assert.AreEqual(nint.Zero, result & appWindowStyle);
    }

    [TestMethod]
    public void AppDoesNotReferenceHeavyWindowsFormsTrayStack()
    {
        var references = typeof(global::CodexQuotaWidget.App.App).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ToArray();

        CollectionAssert.DoesNotContain(references, "System.Windows.Forms");
        CollectionAssert.DoesNotContain(references, "System.Drawing.Common");
    }

    [TestMethod]
    public void IdleMemoryTrimmerDebouncesAndInvokesConfiguredTrim()
    {
        Exception? failure = null;
        var completed = false;
        var thread = new Thread(() =>
        {
            try
            {
                var calls = 0;
                using var trimmer = new IdleMemoryTrimmer(
                    System.Windows.Threading.Dispatcher.CurrentDispatcher,
                    new AppLogger(),
                    TimeSpan.FromSeconds(1),
                    () =>
                    {
                        calls++;
                        return true;
                    });

                trimmer.Schedule();
                trimmer.Schedule();
                Assert.IsTrue(trimmer.TrimNow());
                Assert.AreEqual(1, calls);
                completed = true;
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(10)), "Memory trimmer test timed out.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        Assert.IsTrue(completed);
    }

    [TestMethod]
    public void MainWindow_XamlCanBeConstructed()
    {
        Exception? failure = null;
        var completed = false;
        var thread = new Thread(() =>
        {
            global::CodexQuotaWidget.App.App? application = null;
            UsageModule? module = null;
            TrayIconService? trayIcon = null;
            try
            {
                application = new global::CodexQuotaWidget.App.App();
                application.InitializeComponent();

                var background = Assert.IsInstanceOfType<SolidColorBrush>(
                    application.Resources["WindowBackgroundBrush"]);
                Assert.AreEqual(0x66, background.Color.A);
                var dataFont = Assert.IsInstanceOfType<FontFamily>(
                    application.Resources["DataFont"]);
                Assert.AreEqual("Consolas", dataFont.Source);

                var logger = new AppLogger();
                var settingsService = new SettingsService(logger);
                var placementService = new WindowPlacementService(settingsService, logger);
                module = new UsageModule(new QuotaRefreshCoordinator(new NoOpQuotaProvider()));

                var window = new MainWindow(module, placementService, new AppSettings());
                var expectedReadout = new TaskbarCapsuleHostView();
                Assert.AreEqual(expectedReadout.Width, window.Width);
                Assert.AreEqual(expectedReadout.Height, window.Height);

                var windowHandle = new WindowInteropHelper(window).EnsureHandle();
                Assert.IsTrue(
                    AltTabVisibilityService.IsExcluded(windowHandle),
                    "The widget window must use WS_EX_TOOLWINDOW without WS_EX_APPWINDOW so it is excluded from Alt+Tab.");

                var descendants = EnumerateLogicalDescendants(window)
                    .OfType<FrameworkElement>()
                    .ToArray();
                var labels = descendants
                    .SelectMany(element => element is TextBlock textBlock
                        ? new[] { textBlock.Text, AutomationProperties.GetName(element) }
                        : new[] { AutomationProperties.GetName(element) })
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .ToArray();
                var retiredLabels = new[]
                {
                    "5 小时",
                    "额度重置",
                    "可用额度重置次数",
                    "周额度",
                    "最近更新时间",
                    "每周重置时间",
                    "隐藏到系统托盘",
                };

                foreach (var retiredLabel in retiredLabels)
                {
                    Assert.IsFalse(
                        labels.Any(text => text.Contains(retiredLabel, StringComparison.Ordinal)),
                        $"The weekly-only widget must not expose '{retiredLabel}' in visible text or automation names.");
                }

                Assert.IsFalse(
                    labels.Contains("CODEX", StringComparer.Ordinal),
                    "The previous CODEX header label must not remain visible.");

                var textBlocks = descendants.OfType<TextBlock>().ToArray();
                Assert.HasCount(2, textBlocks);
                Assert.AreEqual("GPT剩余额度", textBlocks[0].Text);
                var percentageBinding = System.Windows.Data.BindingOperations.GetBinding(
                    textBlocks[1],
                    TextBlock.TextProperty);
                Assert.IsNotNull(percentageBinding);
                Assert.AreEqual("WeeklyRemainingText", percentageBinding.Path.Path);
                Assert.IsEmpty(descendants.OfType<Button>());
                Assert.IsEmpty(descendants.OfType<ProgressBar>());
                Assert.IsEmpty(descendants.OfType<Border>());
                Assert.AreEqual(9d, textBlocks[0].FontSize);
                Assert.AreEqual(13d, textBlocks[1].FontSize);

                trayIcon = new TrayIconService(
                    toggleWindow: () => { },
                    toggleTopmost: () => true,
                    refresh: () => Task.CompletedTask,
                    setStartup: enabled => enabled,
                    exit: () => Task.CompletedTask,
                    isWindowVisible: () => true,
                    isTopmost: () => true,
                    isStartupEnabled: () => false,
                    logger);
                trayIcon.RefreshMenuState();

                window.AllowCloseAndClose();
                completed = true;
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                trayIcon?.Dispose();

                if (module is not null)
                {
                    module.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }

                application?.Shutdown();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(15)), "WPF smoke test timed out.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        Assert.IsTrue(completed);
    }

    private static IEnumerable<object> EnumerateLogicalDescendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            yield return child;

            if (child is not DependencyObject dependencyObject)
            {
                continue;
            }

            foreach (var descendant in EnumerateLogicalDescendants(dependencyObject))
            {
                yield return descendant;
            }
        }
    }

    private sealed class NoOpQuotaProvider : IQuotaProvider
    {
#pragma warning disable CS0067
        public event EventHandler? QuotaChanged;
#pragma warning restore CS0067

        public Task<QuotaSnapshot> GetQuotaAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The smoke-test provider must not be queried.");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
