using System.Globalization;
using CodexQuotaWidget.Core;

namespace CodexQuotaWidget.App.Modules;

internal static class QuotaTextFormatter
{
    public static string FormatRemaining(QuotaWindow? window)
    {
        if (window is null)
        {
            return "—";
        }

        var rounded = decimal.Round(window.RemainingPercent, 0, MidpointRounding.AwayFromZero);
        return string.Format(CultureInfo.CurrentCulture, "{0}%", rounded);
    }

    public static string FormatReset(QuotaWindow? window, DateTimeOffset now)
    {
        if (window is null)
        {
            return "等待额度数据";
        }

        var localReset = window.ResetsAt.ToLocalTime();
        var localNow = now.ToLocalTime();
        if (window.WindowDurationMinutes == 10_080)
        {
            return localReset.Year == localNow.Year
                ? $"将于 {localReset:M月d日} 重置"
                : $"将于 {localReset:yyyy年M月d日} 重置";
        }

        if (localReset.Date == localNow.Date)
        {
            return $"将于 {localReset:HH:mm} 重置";
        }

        if (localReset.Date == localNow.Date.AddDays(1))
        {
            return $"将于 明天 {localReset:HH:mm} 重置";
        }

        return localReset.Year == localNow.Year
            ? $"将于 {localReset:M月d日 HH:mm} 重置"
            : $"将于 {localReset:yyyy年M月d日} 重置";
    }

    public static string FormatUpdatedAt(DateTimeOffset? timestamp, DateTimeOffset now)
    {
        if (timestamp is null)
        {
            return "等待首次更新";
        }

        var local = timestamp.Value.ToLocalTime();
        var localNow = now.ToLocalTime();
        return local.Date == localNow.Date ? local.ToString("HH:mm") : local.ToString("M/d HH:mm");
    }

    public static string FormatUpdatedClock(DateTimeOffset? timestamp) => timestamp is { } value
        ? value.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture)
        : "--:--";

}
