using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CodexQuotaWidget.App;
using CodexQuotaWidget.App.Infrastructure;

namespace CodexQuotaWidget.Tests;

[TestClass]
public sealed class PlainQuotaViewTests
{
    [TestMethod]
    public void TwoLineReadout_MatchesTypographyAndMeasuredPadding() => OnSta(() =>
    {
        var view = new TaskbarCapsuleHostView();
        foreach (var percentage in new[] { "—", "0%", "59%", "100%" })
        {
            view.SetContent(new { WeeklyRemainingText = percentage, StatusText = "实时 · 16:00",
                WeeklyResetText = "明天重置", StatusToolTip = "只读查询" });
            view.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            var size = view.FitContent();
            view.Measure(size);
            view.Arrange(new Rect(new Point(), size));
            var grid = (Grid)view.Content;
            Assert.AreEqual(Color.FromArgb(1, 0, 0, 0), ((SolidColorBrush)grid.Background).Color);
            Assert.IsNull(grid.Effect);
            Assert.AreEqual(1, grid.Children.Count);
            var lines = (StackPanel)grid.Children[0];
            Assert.AreEqual(new Thickness(8, 0, 8, 0), lines.Margin);
            Assert.AreEqual(VerticalAlignment.Center, lines.VerticalAlignment);
            Assert.AreEqual(2, lines.Children.Count);
            var caption = (TextBlock)lines.Children[0];
            var text = (TextBlock)lines.Children[1];
            Assert.AreEqual("GPT剩余额度", caption.Text);
            Assert.AreEqual(9d, caption.FontSize);
            Assert.AreEqual(FontWeights.Normal, caption.FontWeight);
            Assert.AreEqual(0.75, caption.Opacity);
            Assert.AreEqual(percentage, text.Text);
            Assert.AreEqual(13d, text.FontSize);
            Assert.AreEqual(FontWeights.SemiBold, text.FontWeight);
            Assert.AreEqual(1d, text.Opacity);
            foreach (var line in new[] { caption, text })
            {
                Assert.AreEqual("Microsoft YaHei UI, Segoe UI", line.FontFamily.Source);
                Assert.AreEqual(HorizontalAlignment.Center, line.HorizontalAlignment);
                Assert.AreEqual(TextWrapping.NoWrap, line.TextWrapping);
                Assert.AreEqual(TextTrimming.None, line.TextTrimming);
                Assert.AreEqual(new Thickness(), line.Margin);
                Assert.IsNull(line.Effect);
                Assert.IsNull(line.Background);
                line.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Assert.IsLessThanOrEqualTo(view.Width - 16, line.DesiredSize.Width);
            }
            Assert.AreEqual(Math.Ceiling(Math.Max(caption.DesiredSize.Width, text.DesiredSize.Width) + 16), view.Width);
            Assert.IsLessThanOrEqualTo(view.Height, caption.DesiredSize.Height + text.DesiredSize.Height);
            Assert.IsTrue(view.UseLayoutRounding);
            Assert.IsTrue(view.SnapsToDevicePixels);
            Assert.AreEqual(Cursors.Hand.ToString(), view.Cursor?.ToString());
            Assert.AreEqual(3, ((StackPanel)grid.ToolTip).Children.Count);
        }
    });

    [TestMethod]
    public void ClickRefresh_UsesTheAssignedReadAction_AndCoalescesRepeatedClicks() => OnSta(() =>
    {
        var view = new TaskbarCapsuleHostView();
        var calls = 0;
        view.RefreshAction = () => { calls++; return Task.CompletedTask; };
        view.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
        Assert.AreEqual(1, calls);
        var completion = new TaskCompletionSource();
        view.RefreshAction = () => { calls++; return completion.Task; };
        var first = view.RequestRefreshAsync();
        view.RequestRefreshAsync().GetAwaiter().GetResult();
        Assert.AreEqual(2, calls);
        completion.SetResult();
        first.GetAwaiter().GetResult();
        view.RefreshAction = null;
        view.RequestRefreshAsync().GetAwaiter().GetResult();
        Assert.AreEqual(2, calls);
    });

    [TestMethod]
    public void ReadoutTheme_UsesTaskbarNormalColors()
    {
        Assert.AreEqual(Color.FromRgb(0x1F, 0x1F, 0x1F), ThemeService.ReadoutColor(false));
        Assert.AreEqual(Colors.White, ThemeService.ReadoutColor(true));
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(10)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
