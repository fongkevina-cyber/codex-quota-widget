using System.Drawing;
using CodexQuotaWidget.App.Infrastructure;

namespace CodexQuotaWidget.Tests;

[TestClass]
public sealed class WindowPlacementServiceTests
{
    [TestMethod]
    public void CalculatePhysicalSize_ConvertsDipsUsingWindowDpi()
    {
        var size = WindowPlacementService.CalculatePhysicalSize(68, 30, 192);

        Assert.AreEqual((136, 60), size);
    }

    [TestMethod]
    public void CalculateRestorePosition_UsesCurrentPhysicalWindowSize()
    {
        var position = WindowPlacementService.CalculateRestorePosition(
            savedLeft: null,
            savedTop: null,
            workingArea: new Rectangle(0, 0, 1920, 1080),
            windowWidthPixels: 136,
            windowHeightPixels: 60);

        Assert.AreEqual((1764, 1000), position);
    }

    [TestMethod]
    public void CalculateRestorePosition_ClampsSavedPositionToWorkingArea()
    {
        var position = WindowPlacementService.CalculateRestorePosition(
            savedLeft: -500,
            savedTop: 1000,
            workingArea: new Rectangle(0, 0, 1920, 1080),
            windowWidthPixels: 136,
            windowHeightPixels: 60);

        Assert.AreEqual((0, 1000), position);
    }
}
