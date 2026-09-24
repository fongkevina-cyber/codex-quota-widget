using CodexQuotaWidget.App.Infrastructure;

namespace CodexQuotaWidget.Tests;

[TestClass]
public sealed class TaskbarOccupancyProviderTests
{
    [TestMethod]
    public void ShouldExcludeFromOccupancy_ExcludesOwnProcessElements()
    {
        Assert.IsTrue(TaskbarOccupancyProvider.ShouldExcludeFromOccupancy(
            elementProcessId: 4242,
            elementWindowHandle: nint.Zero,
            excludeHandle: nint.Zero,
            ownProcessId: 4242));
    }

    [TestMethod]
    public void ShouldExcludeFromOccupancy_ExcludesOwnWindowHandle()
    {
        Assert.IsTrue(TaskbarOccupancyProvider.ShouldExcludeFromOccupancy(
            elementProcessId: 999,
            elementWindowHandle: 1234,
            excludeHandle: 1234,
            ownProcessId: 4242));
    }

    [TestMethod]
    public void ShouldExcludeFromOccupancy_KeepsOtherProcessControls()
    {
        Assert.IsFalse(TaskbarOccupancyProvider.ShouldExcludeFromOccupancy(
            elementProcessId: 999,
            elementWindowHandle: 5678,
            excludeHandle: 1234,
            ownProcessId: 4242));
    }

    [TestMethod]
    public void ShouldExcludeFromOccupancy_NeverExcludesByGeometry()
    {
        // A foreign element whose handle is not the excluded one must be treated as a real
        // taskbar control regardless of where it sits.
        Assert.IsFalse(TaskbarOccupancyProvider.ShouldExcludeFromOccupancy(
            elementProcessId: 1,
            elementWindowHandle: nint.Zero,
            excludeHandle: 1234,
            ownProcessId: 4242));
    }
}