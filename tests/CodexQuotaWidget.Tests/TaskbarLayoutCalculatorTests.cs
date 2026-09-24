using System.Drawing;
using CodexQuotaWidget.App.Infrastructure;

namespace CodexQuotaWidget.Tests;

[TestClass]
public sealed class TaskbarLayoutCalculatorTests
{
    [TestMethod]
    public void TryCalculateHostedPosition_PicksLeftGapBeforeTaskListAt96Dpi()
    {
        var occupied = new[]
        {
            new Rectangle(0, 0, 200, 48),
            new Rectangle(702, 0, 890, 48),
            new Rectangle(1872, 0, 48, 48),
        };

        var found = TaskbarLayoutCalculator.TryCalculateHostedPosition(
            new Rectangle(0, 0, 1920, 48),
            occupied,
            capsuleWidthPixels: 68,
            capsuleHeightPixels: 30,
            out var position);

        Assert.IsTrue(found);
        Assert.AreEqual((206, 9), position);
        Assert.IsLessThan(1920 / 2, position.Left + 68);
    }

    [TestMethod]
    public void TryCalculateHostedPosition_TracksReferenceLayoutAt200Percent()
    {
        var occupied = new[]
        {
            new Rectangle(0, 0, 300, 96),
            new Rectangle(1053, 0, 1335, 96),
            new Rectangle(2808, 0, 72, 96),
        };

        var found = TaskbarLayoutCalculator.TryCalculateHostedPosition(
            new Rectangle(0, 0, 2880, 96),
            occupied,
            capsuleWidthPixels: 136,
            capsuleHeightPixels: 60,
            out var position);

        Assert.IsTrue(found);
        Assert.AreEqual((312, 18), position);
    }

    [TestMethod]
    public void TryCalculateHostedPosition_CentersVerticallyOnBottomTaskbar()
    {
        var occupied = new[]
        {
            new Rectangle(0, 1392, 200, 48),
            new Rectangle(702, 1392, 890, 48),
            new Rectangle(1872, 1392, 48, 48),
        };

        var found = TaskbarLayoutCalculator.TryCalculateHostedPosition(
            new Rectangle(0, 1392, 1920, 48),
            occupied,
            capsuleWidthPixels: 68,
            capsuleHeightPixels: 30,
            out var position);

        Assert.IsTrue(found);
        Assert.AreEqual(1401, position.Top);
    }

    [TestMethod]
    public void TryCalculateHostedPosition_RefusesWhenOccupiedCoversWholeTaskbar()
    {
        var found = TaskbarLayoutCalculator.TryCalculateHostedPosition(
            new Rectangle(0, 0, 1920, 48),
            [new Rectangle(0, 0, 1920, 48)],
            capsuleWidthPixels: 68,
            capsuleHeightPixels: 30,
            out _);

        Assert.IsFalse(found);
    }

    [TestMethod]
    public void TryCalculateHostedPosition_RefusesWhenTaskbarIsTooNarrow()
    {
        var found = TaskbarLayoutCalculator.TryCalculateHostedPosition(
            new Rectangle(0, 0, 700, 48),
            [new Rectangle(0, 0, 700, 48)],
            capsuleWidthPixels: 68,
            capsuleHeightPixels: 30,
            out _);

        Assert.IsFalse(found);
    }

    [TestMethod]
    public void TryCalculateHostedPosition_AcceptsGapExactlyAsWideAsCapsule()
    {
        var found = TaskbarLayoutCalculator.TryCalculateHostedPosition(
            new Rectangle(0, 0, 1000, 48),
            [new Rectangle(0, 0, 100, 48), new Rectangle(180, 0, 820, 48)],
            capsuleWidthPixels: 68,
            capsuleHeightPixels: 30,
            out var position);

        Assert.IsTrue(found);
        Assert.AreEqual(106, position.Left);
    }

    [TestMethod]
    public void TryCalculateHostedPosition_RefusesGapOnePixelTooNarrow()
    {
        var found = TaskbarLayoutCalculator.TryCalculateHostedPosition(
            new Rectangle(0, 0, 1000, 48),
            [new Rectangle(0, 0, 100, 48), new Rectangle(179, 0, 821, 48)],
            capsuleWidthPixels: 68,
            capsuleHeightPixels: 30,
            out _);

        Assert.IsFalse(found);
    }

    [TestMethod]
    public void TryCalculateHostedPosition_IgnoresRegionsOutsideCapsuleBand()
    {
        var occupied = new[]
        {
            new Rectangle(0, 0, 1000, 9),
            new Rectangle(0, 39, 1000, 9),
        };

        var found = TaskbarLayoutCalculator.TryCalculateHostedPosition(
            new Rectangle(0, 0, 1000, 48),
            occupied,
            capsuleWidthPixels: 68,
            capsuleHeightPixels: 30,
            out var position);

        Assert.IsTrue(found);
        Assert.AreEqual((7, 9), position);
    }
}
