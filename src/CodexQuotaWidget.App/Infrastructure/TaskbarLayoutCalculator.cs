using System.Drawing;

namespace CodexQuotaWidget.App.Infrastructure;

/// <summary>
/// Chooses where the capsule sits inside the taskbar using the real occupied regions
/// reported by <see cref="ITaskbarOccupancyProvider"/>. The capsule only lands in a gap
/// wide enough to hold it; when no such gap exists the host refuses to attach rather than
/// covering an existing taskbar control.
/// </summary>
internal static class TaskbarLayoutCalculator
{
    internal const double EdgeMarginHeightFactor = 0.15;
    internal const double PaddingHeightFactor = 0.12;

    public static bool TryCalculateHostedPosition(
        Rectangle taskbarBounds,
        IReadOnlyList<Rectangle> occupiedRegions,
        int capsuleWidthPixels,
        int capsuleHeightPixels,
        out (int Left, int Top) position)
    {
        position = default;

        var taskbarWidth = Math.Max(1, taskbarBounds.Width);
        var taskbarHeight = Math.Max(1, taskbarBounds.Height);
        var width = Math.Max(1, capsuleWidthPixels);
        var height = Math.Max(1, capsuleHeightPixels);

        var bandTop = taskbarBounds.Top + Math.Max(0, (taskbarHeight - height) / 2);
        var bandBottom = bandTop + height;
        var edgeMargin = Math.Max(1, (int)Math.Round(taskbarHeight * EdgeMarginHeightFactor));
        var padding = Math.Max(1, (int)Math.Round(taskbarHeight * PaddingHeightFactor));

        var intervals = new List<(int Start, int End)>();
        foreach (var region in occupiedRegions)
        {
            if (region.Bottom <= bandTop || region.Top >= bandBottom)
            {
                continue;
            }

            intervals.Add((region.Left - padding, region.Right + padding));
        }

        intervals.Sort(static (left, right) => left.Start.CompareTo(right.Start));

        var searchLeft = taskbarBounds.Left + edgeMargin;
        var searchRight = taskbarBounds.Right - edgeMargin;
        var cursor = searchLeft;

        foreach (var interval in intervals)
        {
            if (interval.End <= cursor)
            {
                continue;
            }

            var gapEnd = Math.Min(interval.Start, searchRight);
            if (gapEnd - cursor >= width)
            {
                position = (cursor, bandTop);
                return true;
            }

            cursor = Math.Max(cursor, interval.End);
        }

        if (searchRight - cursor >= width)
        {
            position = (cursor, bandTop);
            return true;
        }

        return false;
    }
}
