using System.IO;
using CodexQuotaWidget.App.Infrastructure;
using CodexQuotaWidget.App.Modules;
using CodexQuotaWidget.Core;

namespace CodexQuotaWidget.App;

public partial class App
{
    // Same production coordinator and bindings, but no real account, saved settings,
    // startup changes, or interaction with the installed widget.
    private async Task<int> RunTaskbarWidgetTestAsync()
    {
        var logger = new AppLogger(Path.Combine(Path.GetTempPath(), "CodexQuotaWidget-taskbar-widget-test.log"));
        using var theme = new ThemeService(logger);
        theme.Start();
        var provider = new FixedQuotaProvider(88);
        await using var module = new UsageModule(new QuotaRefreshCoordinator(provider));
        var settings = new SettingsService(logger, Path.Combine(Path.GetTempPath(), "unused-test-settings.json"));
        var window = new MainWindow(module,
            new WindowPlacementService(settings, logger, persistSettings: false),
            new AppSettings { HostMode = WindowHostMode.Floating });
        using var presentation = new WidgetPresentationCoordinator(WindowHostMode.Taskbar, module,
            () => new TaskbarCapsuleHost(new TaskbarCapsuleEnvironment(), new HwndSourceCapsuleSurfaceFactory(), logger),
            visible =>
            {
                if (visible && !window.IsVisible) window.ShowFromTray();
                else if (!visible && window.IsVisible) window.HideToTray();
            }, _ => { }, logger);
        void CheckHosted(string phase)
        {
            if (!presentation.IsVisible || presentation.HostHandle == nint.Zero
                || TaskbarNativeMethods.GetAncestor(presentation.HostHandle, TaskbarNativeMethods.ParentAncestor)
                    != TaskbarNativeMethods.FindWindow("Shell_TrayWnd", null))
                throw new InvalidOperationException("Production taskbar presentation unavailable.");
            logger.Write(phase);
        }
        try
        {
            await module.StartAsync();
            presentation.Reconcile();
            CheckHosted("phase_hosted_88");
            await Task.Delay(12000);
            presentation.SetVisible(false);
            logger.Write("phase_hidden");
            await Task.Delay(2000);
            provider.RemainingPercent = 77;
            await module.RefreshAsync();
            presentation.SetVisible(true);
            CheckHosted("phase_shown_77");
            await Task.Delay(12000);
            presentation.SetMode(WindowHostMode.Floating);
            if (!window.IsVisible || presentation.HostHandle != nint.Zero)
                throw new InvalidOperationException("Floating transition failed.");
            logger.Write("phase_floating");
            await Task.Delay(6000);
            provider.RemainingPercent = 66;
            await module.RefreshAsync();
            presentation.SetMode(WindowHostMode.Taskbar);
            CheckHosted("phase_hosted_66");
            await Task.Delay(12000);
            provider.RemainingPercent = 55;
            await module.RefreshAsync();
            presentation.RecreateHost();
            CheckHosted("phase_recreated_55");
            await Task.Delay(16000);
            presentation.Dispose();
            logger.Write("cleanup");
            return 0;
        }
        catch (Exception ex)
        {
            logger.Write("widget_test_failed", ex);
            return 3;
        }
        finally { window.AllowCloseAndClose(preservePlacement: true); }
    }
}
