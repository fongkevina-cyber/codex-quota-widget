using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using MediaColor = System.Windows.Media.Color;
using WpfSystemColors = System.Windows.SystemColors;

namespace CodexQuotaWidget.App.Infrastructure;

public sealed class ThemeService : IDisposable
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private readonly AppLogger _logger;
    private bool _started;

    public ThemeService(AppLogger logger)
    {
        _logger = logger;
    }

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
        ApplyCurrentTheme();
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) => DispatchApply();

    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(SystemParameters.HighContrast))
        {
            DispatchApply();
        }
    }

    private void DispatchApply()
    {
        var application = System.Windows.Application.Current;
        if (application is null)
        {
            return;
        }

        application.Dispatcher.BeginInvoke(ApplyCurrentTheme);
    }

    private void ApplyCurrentTheme()
    {
        try
        {
            if (SystemParameters.HighContrast)
            {
                ApplyHighContrastTheme();
                return;
            }

            ApplyPalette(IsDarkTheme() ? DarkPalette : LightPalette);
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey, writable: false);
            var darkTaskbar = key?.GetValue("SystemUsesLightTheme") is int value && value == 0;
            System.Windows.Application.Current.Resources["ReadoutTextBrush"] = FrozenBrush(ReadoutColor(darkTaskbar));
        }
        catch (Exception ex)
        {
            _logger.Write("theme_apply_failed", ex);
            ApplyPalette(LightPalette);
            System.Windows.Application.Current.Resources["ReadoutTextBrush"] = FrozenBrush(ReadoutColor(false));
        }
    }

    private static bool IsDarkTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey, writable: false);
        return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
    }

    internal static MediaColor ReadoutColor(bool dark) => dark
        ? MediaColor.FromRgb(0xFF, 0xFF, 0xFF) : MediaColor.FromRgb(0x1F, 0x1F, 0x1F);

    private static void ApplyPalette(IReadOnlyDictionary<string, MediaColor> palette)
    {
        var resources = System.Windows.Application.Current.Resources;
        foreach (var (name, color) in palette)
        {
            resources[name] = FrozenBrush(color);
        }
    }

    private static void ApplyHighContrastTheme()
    {
        var resources = System.Windows.Application.Current.Resources;
        resources["WindowBackgroundBrush"] = FrozenClone(WpfSystemColors.WindowBrush);
        resources["SurfaceBrush"] = FrozenClone(WpfSystemColors.ControlBrush);
        resources["TextPrimaryBrush"] = FrozenClone(WpfSystemColors.WindowTextBrush);
        resources["ReadoutTextBrush"] = FrozenClone(WpfSystemColors.WindowTextBrush);
        resources["TextSecondaryBrush"] = FrozenClone(WpfSystemColors.GrayTextBrush);
        resources["DividerBrush"] = FrozenClone(WpfSystemColors.ControlDarkBrush);
        resources["ProgressTrackBrush"] = FrozenClone(WpfSystemColors.ControlDarkBrush);
        resources["DataAccentBrush"] = FrozenClone(WpfSystemColors.HighlightBrush);
        resources["StatusLiveBrush"] = FrozenClone(WpfSystemColors.HighlightBrush);
        resources["StatusWarningBrush"] = FrozenClone(WpfSystemColors.HotTrackBrush);
        resources["StatusOfflineBrush"] = FrozenClone(WpfSystemColors.GrayTextBrush);
        resources["DangerBrush"] = FrozenClone(WpfSystemColors.HighlightBrush);
        resources["PillBackgroundBrush"] = FrozenClone(WpfSystemColors.HighlightBrush);
        resources["PillForegroundBrush"] = FrozenClone(WpfSystemColors.HighlightTextBrush);
        resources["HoverBrush"] = FrozenClone(WpfSystemColors.ControlLightBrush);
        resources["WindowBorderBrush"] = FrozenClone(WpfSystemColors.WindowTextBrush);
    }

    private static SolidColorBrush FrozenBrush(MediaColor color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush FrozenClone(SolidColorBrush source) => FrozenBrush(source.Color);

    private static readonly IReadOnlyDictionary<string, MediaColor> LightPalette = new Dictionary<string, MediaColor>
    {
        ["WindowBackgroundBrush"] = MediaColor.FromArgb(0x66, 0xF4, 0xF6, 0xF7),
        ["SurfaceBrush"] = MediaColor.FromArgb(0xB3, 0xFA, 0xFB, 0xFB),
        ["TextPrimaryBrush"] = MediaColor.FromRgb(0x17, 0x1A, 0x1F),
        ["TextSecondaryBrush"] = MediaColor.FromRgb(0x66, 0x71, 0x7A),
        ["DividerBrush"] = MediaColor.FromArgb(0x80, 0xDC, 0xE2, 0xE6),
        ["ProgressTrackBrush"] = MediaColor.FromArgb(0x99, 0xDC, 0xE2, 0xE6),
        ["DataAccentBrush"] = MediaColor.FromRgb(0x4F, 0x6E, 0xF7),
        ["StatusLiveBrush"] = MediaColor.FromRgb(0x0B, 0x7A, 0x49),
        ["StatusWarningBrush"] = MediaColor.FromRgb(0xC4, 0x84, 0x1D),
        ["StatusOfflineBrush"] = MediaColor.FromRgb(0x77, 0x82, 0x8A),
        ["DangerBrush"] = MediaColor.FromRgb(0xB5, 0x46, 0x3A),
        ["PillBackgroundBrush"] = MediaColor.FromArgb(0xB8, 0xDD, 0xF2, 0xE7),
        ["PillForegroundBrush"] = MediaColor.FromRgb(0x0B, 0x7A, 0x49),
        ["HoverBrush"] = MediaColor.FromArgb(0x99, 0xE7, 0xEB, 0xEE),
        ["WindowBorderBrush"] = MediaColor.FromArgb(0x66, 0xDC, 0xE2, 0xE6)
    };

    private static readonly IReadOnlyDictionary<string, MediaColor> DarkPalette = new Dictionary<string, MediaColor>
    {
        ["WindowBackgroundBrush"] = MediaColor.FromArgb(0x66, 0x21, 0x26, 0x2B),
        ["SurfaceBrush"] = MediaColor.FromArgb(0xB3, 0x20, 0x24, 0x2A),
        ["TextPrimaryBrush"] = MediaColor.FromRgb(0xF4, 0xF6, 0xF7),
        ["TextSecondaryBrush"] = MediaColor.FromRgb(0xAA, 0xB3, 0xBA),
        ["DividerBrush"] = MediaColor.FromArgb(0x99, 0x38, 0x40, 0x47),
        ["ProgressTrackBrush"] = MediaColor.FromArgb(0xB3, 0x38, 0x40, 0x47),
        ["DataAccentBrush"] = MediaColor.FromRgb(0x7F, 0x96, 0xFF),
        ["StatusLiveBrush"] = MediaColor.FromRgb(0x69, 0xDD, 0xA7),
        ["StatusWarningBrush"] = MediaColor.FromRgb(0xE0, 0xAD, 0x59),
        ["StatusOfflineBrush"] = MediaColor.FromRgb(0x8F, 0x99, 0xA1),
        ["DangerBrush"] = MediaColor.FromRgb(0xEF, 0x7A, 0x6E),
        ["PillBackgroundBrush"] = MediaColor.FromArgb(0xB8, 0x18, 0x46, 0x34),
        ["PillForegroundBrush"] = MediaColor.FromRgb(0x69, 0xDD, 0xA7),
        ["HoverBrush"] = MediaColor.FromArgb(0x99, 0x2B, 0x31, 0x38),
        ["WindowBorderBrush"] = MediaColor.FromArgb(0x66, 0x38, 0x40, 0x47)
    };

    public void Dispose()
    {
        if (!_started)
        {
            return;
        }

        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        _started = false;
    }
}
