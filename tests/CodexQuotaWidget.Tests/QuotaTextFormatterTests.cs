using CodexQuotaWidget.App.Modules;
using CodexQuotaWidget.Core;

namespace CodexQuotaWidget.Tests;

[TestClass]
public sealed class QuotaTextFormatterTests
{
    [TestMethod]
    public void WeeklyWindowShowsDateWithoutTimeInCurrentYear()
    {
        var now = LocalDate(2026, 7, 10, 12, 0);
        var reset = LocalDate(2026, 7, 17, 8, 34);
        var window = new QuotaWindow(10_080, 3m, reset);

        Assert.AreEqual("将于 7月17日 重置", QuotaTextFormatter.FormatReset(window, now));
    }

    [TestMethod]
    public void WeeklyWindowIncludesYearWhenCrossingIntoAnotherYear()
    {
        var now = LocalDate(2026, 12, 30, 12, 0);
        var reset = LocalDate(2027, 1, 6, 8, 34);
        var window = new QuotaWindow(10_080, 3m, reset);

        Assert.AreEqual("将于 2027年1月6日 重置", QuotaTextFormatter.FormatReset(window, now));
    }

    [TestMethod]
    public void FiveHourWindowUsesTodayAndLocalClock()
    {
        var now = LocalDate(2026, 7, 10, 15, 0);
        var reset = LocalDate(2026, 7, 10, 19, 10);
        var window = new QuotaWindow(300, 13m, reset);

        Assert.AreEqual("将于 19:10 重置", QuotaTextFormatter.FormatReset(window, now));
    }

    [TestMethod]
    public void RemainingPercentageRoundsHalfAwayFromZero()
    {
        var window = new QuotaWindow(300, 2.5m, LocalDate(2026, 7, 10, 19, 10));

        Assert.AreEqual("98%", QuotaTextFormatter.FormatRemaining(window));
    }

    [TestMethod]
    public void UpdatedClockUsesLocalHourAndMinuteAndHasCompactEmptyState()
    {
        var updatedAt = LocalDate(2026, 7, 10, 9, 7);

        Assert.AreEqual("09:07", QuotaTextFormatter.FormatUpdatedClock(updatedAt));
        Assert.AreEqual("--:--", QuotaTextFormatter.FormatUpdatedClock(null));
    }

    private static DateTimeOffset LocalDate(int year, int month, int day, int hour, int minute)
    {
        var local = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }
}
