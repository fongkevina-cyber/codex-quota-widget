using System.Drawing;
using CodexQuotaWidget.App.Infrastructure;

namespace CodexQuotaWidget.Tests;

[TestClass]
public sealed class TaskbarCapsuleHostTests
{
    private const nint TaskbarHandle = 10;

    [TestMethod]
    public void TryCreate_WhenNoSafeSlot_RefusesWithoutCreatingSurface()
    {
        var environment = new FakeEnvironment
        {
            Occupied = [new Rectangle(0, 0, 1920, 48)],
        };
        var factory = new FakeFactory();
        var host = CreateHost(environment, factory);

        var created = host.TryCreate("88%", out var reason);

        Assert.IsFalse(created);
        Assert.AreEqual("no_safe_slot", reason);
        Assert.AreEqual(0, factory.CreateCalls);
        Assert.IsFalse(host.IsAlive);
    }

    [TestMethod]
    public void TryCreate_WhenOccupancyUnavailable_Refuses()
    {
        var environment = new FakeEnvironment { OccupancyAvailable = false };
        var host = CreateHost(environment, new FakeFactory());

        Assert.IsFalse(host.TryCreate("88%", out var reason));
        Assert.AreEqual("occupancy_unavailable", reason);
    }

    [TestMethod]
    public void TryCreate_PlansSlotAndCreatesChildSurface()
    {
        var environment = new FakeEnvironment();
        var factory = new FakeFactory();
        var host = CreateHost(environment, factory);

        Assert.IsTrue(host.TryCreate("88%", out var reason));
        Assert.AreEqual(string.Empty, reason);
        Assert.AreEqual(1, factory.CreateCalls);
        Assert.IsTrue(host.IsAlive);
        Assert.IsTrue(host.IsHidden);
        Assert.AreEqual(TaskbarHandle, host.TaskbarHandle);
        Assert.AreEqual(TaskbarHandle, factory.LastParent);
        Assert.AreEqual((206, 9), (factory.LastX, factory.LastY));
        Assert.AreEqual("88%", factory.Surface!.Percentage);
    }

    [TestMethod]
    public void ShowHideAndRefresh_NeverChangeVisibilityFromRefresh()
    {
        var environment = new FakeEnvironment();
        var factory = new FakeFactory();
        var host = CreateHost(environment, factory);
        Assert.IsTrue(host.TryCreate("88%", out _));
        var surface = factory.Surface!;

        Assert.IsTrue(host.Show());
        Assert.AreEqual(1, surface.ShowCalls);
        Assert.IsFalse(host.IsHidden);

        var generation = host.Generation;

        Assert.IsTrue(host.Hide());
        Assert.AreEqual(1, surface.HideCalls);
        Assert.IsTrue(host.IsHidden);

        // Repaint must not re-show a hidden capsule.
        Assert.IsFalse(host.TryRefresh(generation));
        Assert.AreEqual(0, surface.RedrawCalls);
        Assert.AreEqual(1, surface.ShowCalls);
        Assert.IsTrue(host.IsHidden);
    }

    [TestMethod]
    public void Destroy_DisposesSurfaceAndInvalidatesStaleCallbacks()
    {
        var environment = new FakeEnvironment();
        var factory = new FakeFactory();
        var host = CreateHost(environment, factory);
        Assert.IsTrue(host.TryCreate("88%", out _));
        var surface = factory.Surface!;
        var generation = host.Generation;

        host.Destroy();

        Assert.AreEqual(1, surface.DisposeCalls);
        Assert.IsFalse(host.IsAlive);
        Assert.IsFalse(host.TryRefresh(generation));
        Assert.IsFalse(host.Show());
        Assert.IsFalse(host.Hide());
        Assert.AreEqual(0, surface.ShowCalls);
        Assert.AreEqual(0, surface.HideCalls);
    }

    [TestMethod]
    public void Recreate_UsesFreshGenerationAndRejectsOldHost()
    {
        var environment = new FakeEnvironment();
        var factory = new FakeFactory();
        var first = CreateHost(environment, factory);
        Assert.IsTrue(first.TryCreate("88%", out _));
        var firstGeneration = first.Generation;
        first.Destroy();

        var second = CreateHost(environment, factory);
        Assert.IsTrue(second.TryCreate("66%", out _));

        // Each host owns its own guard; the destroyed host rejects its old generation and
        // the new host is fully usable.
        Assert.IsFalse(first.TryRefresh(firstGeneration));
        Assert.IsTrue(second.IsAlive);
        Assert.IsTrue(second.Show());
        Assert.IsTrue(second.TryRefresh(second.Generation));
    }

    [TestMethod]
    public void Destroy_IsIdempotent()
    {
        var factory = new FakeFactory();
        var host = CreateHost(new FakeEnvironment(), factory);
        Assert.IsTrue(host.TryCreate("88%", out _));

        host.Destroy();
        host.Destroy();

        Assert.AreEqual(1, factory.Surface!.DisposeCalls);
    }

    [TestMethod]
    public void LayoutFailure_HidesAndRecoversWithoutReshowingOnRefresh()
    {
        var environment = new FakeEnvironment();
        var host = CreateHost(environment, new FakeFactory());
        Assert.IsTrue(host.TryCreate("88%", out _));
        Assert.IsTrue(host.Show());
        environment.OccupancyAvailable = false;
        Assert.IsFalse(host.TryReposition());
        Assert.IsTrue(host.IsHidden);
        Assert.IsFalse(host.TryRefresh(host.Generation));
        environment.OccupancyAvailable = true;
        Assert.IsTrue(host.Show());
        Assert.IsFalse(host.IsHidden);
    }

    [TestMethod]
    public void Coordinator_PreservesUserIntentAcrossLayoutFailureAndRecreation()
    {
        var environment = new FakeEnvironment();
        var factory = new FakeFactory();
        var content = new object();
        var floating = false;
        var saved = new List<WindowHostMode>();
        using var presentation = new WidgetPresentationCoordinator(WindowHostMode.Taskbar, content,
            () => CreateHost(environment, factory), value => floating = value, saved.Add,
            new AppLogger(Path.Combine(Path.GetTempPath(), "presentation-tests.log")));
        presentation.Reconcile();
        Assert.IsTrue(presentation.IsVisible);
        Assert.AreSame(content, factory.Surface!.Content);
        Assert.IsFalse(floating);
        environment.OccupancyAvailable = false;
        presentation.Reconcile();
        Assert.IsFalse(presentation.IsVisible);
        Assert.IsTrue(presentation.WantsVisible);
        environment.OccupancyAvailable = true;
        presentation.Reconcile();
        Assert.IsTrue(presentation.IsVisible);
        presentation.SetVisible(false);
        presentation.RecreateHost();
        presentation.Reconcile();
        Assert.IsFalse(presentation.IsVisible);
        Assert.AreEqual(1, factory.CreateCalls);
        presentation.SetVisible(true);
        Assert.AreEqual(2, factory.CreateCalls);
        Assert.IsTrue(presentation.IsVisible);
        presentation.SetMode(WindowHostMode.Floating);
        Assert.IsTrue(floating);
        Assert.AreEqual(nint.Zero, presentation.HostHandle);
        presentation.SetVisible(false);
        presentation.SetMode(WindowHostMode.Taskbar);
        Assert.IsFalse(floating);
        Assert.IsFalse(presentation.IsVisible);
        CollectionAssert.AreEqual(new[] { WindowHostMode.Floating, WindowHostMode.Taskbar }, saved);
        presentation.Dispose();
        presentation.SetVisible(true);
        Assert.IsFalse(presentation.IsVisible);
    }

    [TestMethod]
    public void Coordinator_RecoversDestroyedNativeHandleWithoutRecreatingData()
    {
        var environment = new FakeEnvironment();
        var factory = new FakeFactory();
        var content = new object();
        using var presentation = new WidgetPresentationCoordinator(WindowHostMode.Taskbar, content,
            () => CreateHost(environment, factory), _ => { }, _ => Assert.Fail("Recovery must not change settings"),
            new AppLogger(Path.Combine(Path.GetTempPath(), "presentation-tests.log")));
        presentation.Reconcile();
        var first = factory.Surface!;
        environment.HostAlive = false;
        presentation.Reconcile();
        Assert.IsFalse(presentation.IsVisible);
        Assert.AreEqual(1, first.DisposeCalls);
        environment.HostAlive = true;
        presentation.Reconcile();
        Assert.IsTrue(presentation.IsVisible);
        Assert.AreSame(content, factory.Surface!.Content);
    }

    private static TaskbarCapsuleHost CreateHost(FakeEnvironment environment, FakeFactory factory) =>
        new(environment, factory, new AppLogger(Path.Combine(Path.GetTempPath(), "CodexQuotaWidget-capsule-host-tests.log")));

    private sealed class FakeEnvironment : ITaskbarCapsuleEnvironment
    {
        public bool OccupancyAvailable { get; set; } = true;
        public bool HostAlive { get; set; } = true;

        public List<Rectangle> Occupied { get; set; } =
        [
            new Rectangle(0, 0, 200, 48),
            new Rectangle(702, 0, 890, 48),
            new Rectangle(1872, 0, 48, 48),
        ];

        public nint FindTaskbar() => TaskbarHandle;

        public bool IsWindow(nint windowHandle) => windowHandle != nint.Zero
            && (windowHandle != 4242 || HostAlive);

        public bool TryGetBounds(nint windowHandle, out TaskbarRect bounds)
        {
            bounds = new TaskbarRect { Left = 0, Top = 0, Right = 1920, Bottom = 48 };
            return true;
        }

        public uint GetDpi(nint taskbarHandle) => 96;

        public bool TryGetOccupied(
            nint taskbarHandle,
            nint excludeHandle,
            out IReadOnlyList<Rectangle> occupiedRegions)
        {
            occupiedRegions = Occupied;
            return OccupancyAvailable;
        }

        public TaskbarPoint ToClient(nint taskbarHandle, int x, int y) => new() { X = x, Y = y };

        public nint GetRealParent(nint windowHandle) => TaskbarHandle;
    }

    private sealed class FakeFactory : ITaskbarCapsuleSurfaceFactory
    {
        public System.Windows.Size Measure(string percentage) => new(40, 30);
        public int CreateCalls { get; private set; }
        public nint LastParent { get; private set; }
        public int LastX { get; private set; }
        public int LastY { get; private set; }
        public FakeSurface? Surface { get; private set; }

        public bool TryCreate(
            nint parentHandle,
            int x,
            int y,
            int width,
            int height,
            out ITaskbarCapsuleSurface? surface,
            out string mode,
            out string reason)
        {
            CreateCalls++;
            LastParent = parentHandle;
            LastX = x;
            LastY = y;
            Surface = new FakeSurface();
            surface = Surface;
            mode = "per_pixel";
            reason = string.Empty;
            return true;
        }
    }

    private sealed class FakeSurface : ITaskbarCapsuleSurface
    {
        public System.Windows.Size Measure() => new(40, 30);
        public nint Handle => 4242;
        public int ShowCalls { get; private set; }
        public int HideCalls { get; private set; }
        public int RedrawCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public string Percentage { get; private set; } = string.Empty;

        public void Show() => ShowCalls++;
        public void Hide() => HideCalls++;
        public void RaiseAndRedraw() => RedrawCalls++;
        public void SetPercentage(string percentage) => Percentage = percentage;
        public object? Content { get; private set; }
        public void SetContent(object dataContext) => Content = dataContext;
        public bool Move(int x, int y, int width, int height) => true;
        public void Dispose() => DisposeCalls++;
    }
}
