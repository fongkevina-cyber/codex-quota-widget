using System.Windows.Controls;
using System.Windows;
using System.Windows.Input;
using CodexQuotaWidget.App.Infrastructure;

namespace CodexQuotaWidget.App;

/// <summary>
/// Measured two-line transparent readout, shared by taskbar and floating modes.
/// Keeps the caption and percentage on their default line heights and binds by name so
/// the same <c>UsageModule</c> (or a MOCK view model) drives both views.
/// </summary>
public partial class TaskbarCapsuleHostView : UserControl
{
    private bool _refreshing;
    internal Func<Task>? RefreshAction { get; set; }

    public TaskbarCapsuleHostView()
    {
        InitializeComponent();
        FitContent();
    }

    public void SetContent(object? dataContext)
    {
        DataContext = dataContext;
        FitContent();
    }

    internal Size FitContent()
    {
        Percentage.GetBindingExpression(TextBlock.TextProperty)?.UpdateTarget();
        var unlimited = new Size(double.PositiveInfinity, double.PositiveInfinity);
        Caption.Measure(unlimited);
        Percentage.Measure(unlimited);
        Width = Math.Ceiling(Math.Max(Caption.DesiredSize.Width, Percentage.DesiredSize.Width) + 16);
        Height = Math.Ceiling(Math.Max(34, Caption.DesiredSize.Height + Percentage.DesiredSize.Height));
        return new Size(Width, Height);
    }

    private async void Readout_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (RefreshAction is null) return;
        e.Handled = true;
        await RequestRefreshAsync();
    }

    internal async Task RequestRefreshAsync()
    {
        if (_refreshing || RefreshAction is null) return;
        _refreshing = true;
        try { await RefreshAction(); }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { new AppLogger().Write("readout_refresh_failed", ex); }
        finally { _refreshing = false; }
    }
}
