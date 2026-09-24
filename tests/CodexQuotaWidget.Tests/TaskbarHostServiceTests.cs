using System.Drawing;
using CodexQuotaWidget.App.Infrastructure;

namespace CodexQuotaWidget.Tests;

[TestClass]
public sealed class TaskbarHostServiceTests
{
    private const nint WindowHandle = 5;
    private const nint TaskbarHandle = 10;
    private const nint ReplacementTaskbarHandle = 20;
    private const nint WindowOwner = 77;
    private const long ChildStyle = 0x40000000L;
    private const long PopupStyle = unchecked((long)0x80000000L);
    private const long TopmostExtendedStyle = 0x00000008L;
    private static readonly nint PopupStyleValue = unchecked((nint)PopupStyle);

    [TestMethod]
    public void AttachToTaskbar_WhenTaskbarMissing_FailsWithoutChangingParent()
    {
        var native = new FakeTaskbarNative { TaskbarExists = false };
        native.AliveHandles.Add(WindowHandle);
        var service = CreateService(native, new FakeOccupancy());

        var attached = service.AttachToTaskbar(WindowHandle, 68, 30);

        Assert.IsFalse(attached);
        Assert.AreEqual(nint.Zero, native.GetRealParent(WindowHandle));
        Assert.IsEmpty(native.Moves);
    }

    [TestMethod]
    public void AttachToTaskbar_WhenOccupancyUnavailable_RefusesAndLeavesWindowUntouched()
    {
        var native = CreateAttachableNative();
        var occupancy = new FakeOccupancy { Available = false };
        var service = CreateService(native, occupancy);

        var attached = service.AttachToTaskbar(WindowHandle, 68, 30);

        Assert.IsFalse(attached);
        Assert.AreEqual(nint.Zero, native.GetRealParent(WindowHandle));
        Assert.AreEqual(PopupStyleValue, native.GetStyle(WindowHandle));
        Assert.IsFalse(service.IsHosted);
        Assert.IsFalse(service.IsTaskbarOwned);
    }

    [TestMethod]
    public void AttachToTaskbar_WhenNoSafeGap_RefusesInsteadOfCoveringButtons()
    {
        var native = CreateAttachableNative();
        var occupancy = new FakeOccupancy
        {
            Regions = [new Rectangle(0, 0, 1920, 48)],
        };
        var service = CreateService(native, occupancy);

        var attached = service.AttachToTaskbar(WindowHandle, 68, 30);

        Assert.IsFalse(attached);
        Assert.AreEqual(nint.Zero, native.GetRealParent(WindowHandle));
        Assert.AreEqual(PopupStyleValue, native.GetStyle(WindowHandle));
        Assert.IsEmpty(native.Moves);
    }

    [TestMethod]
    public void AttachToTaskbar_ConvertsToRealChildAndClearsTopmost()
    {
        var native = CreateAttachableNative();
        var service = CreateService(native, CreateDefaultOccupancy());

        var attached = service.AttachToTaskbar(WindowHandle, 68, 30);

        Assert.IsTrue(attached);
        Assert.AreEqual(TaskbarHandle, native.GetRealParent(WindowHandle));
        Assert.AreNotEqual(0L, (long)native.GetStyle(WindowHandle) & ChildStyle);
        Assert.AreEqual(0L, (long)native.GetStyle(WindowHandle) & PopupStyle);
        Assert.AreEqual(0L, (long)native.GetExtendedStyle(WindowHandle) & TopmostExtendedStyle);
        var (handle, x, y, width, height) = native.Moves.Single();
        Assert.AreEqual(WindowHandle, handle);
        Assert.AreEqual(206, x);
        Assert.AreEqual(9, y);
        Assert.AreEqual(68, width);
        Assert.AreEqual(30, height);
        Assert.IsTrue(service.IsHosted);
        Assert.IsTrue(service.IsTaskbarOwned);
    }

    [TestMethod]
    public void AttachToTaskbar_IsIdempotentAndKeepsOriginalFloatingSnapshot()
    {
        var native = CreateAttachableNative();
        native.Owners[WindowHandle] = WindowOwner;
        var service = CreateService(native, CreateDefaultOccupancy());

        Assert.IsTrue(service.AttachToTaskbar(WindowHandle, 68, 30));
        // A second attach for the same HWND/host must reposition, not re-capture styles.
        Assert.IsTrue(service.AttachToTaskbar(WindowHandle, 68, 30));
        Assert.IsTrue(service.IsHosted);

        Assert.IsTrue(service.DetachToDesktop(WindowHandle));
        Assert.AreNotEqual(TaskbarHandle, native.GetRealParent(WindowHandle));
        Assert.AreEqual(PopupStyleValue, native.GetStyle(WindowHandle));
        Assert.AreEqual((nint)TopmostExtendedStyle, native.GetExtendedStyle(WindowHandle));
        Assert.AreEqual(WindowOwner, native.GetOwner(WindowHandle));
    }

    [TestMethod]
    public void AttachToTaskbar_MovingToReplacementTaskbarKeepsOriginalSnapshot()
    {
        var native = CreateAttachableNative();
        native.Owners[WindowHandle] = WindowOwner;
        var service = CreateService(native, CreateDefaultOccupancy());
        Assert.IsTrue(service.AttachToTaskbar(WindowHandle, 68, 30));

        native.AliveHandles.Add(ReplacementTaskbarHandle);
        native.TaskbarHandle = ReplacementTaskbarHandle;
        native.SetParentDirect(WindowHandle, nint.Zero);

        Assert.IsTrue(service.AttachToTaskbar(WindowHandle, 68, 30));
        Assert.AreEqual(ReplacementTaskbarHandle, native.GetRealParent(WindowHandle));
        Assert.IsTrue(service.IsHosted);

        Assert.IsTrue(service.DetachToDesktop(WindowHandle));
        Assert.AreNotEqual(ReplacementTaskbarHandle, native.GetRealParent(WindowHandle));
        Assert.AreEqual(PopupStyleValue, native.GetStyle(WindowHandle));
        Assert.AreEqual(WindowOwner, native.GetOwner(WindowHandle));
    }

    [TestMethod]
    public void AttachToTaskbar_WhenSetParentRejected_RestoresOriginalStyles()
    {
        var native = CreateAttachableNative();
        native.SetParentFails = true;
        var service = CreateService(native, CreateDefaultOccupancy());

        var attached = service.AttachToTaskbar(WindowHandle, 68, 30);

        Assert.IsFalse(attached);
        Assert.AreEqual(nint.Zero, native.GetRealParent(WindowHandle));
        Assert.AreEqual(PopupStyleValue, native.GetStyle(WindowHandle));
        Assert.AreEqual((nint)TopmostExtendedStyle, native.GetExtendedStyle(WindowHandle));
        Assert.IsFalse(service.IsHosted);
    }

    [TestMethod]
    public void AttachToTaskbar_ScalesSizeAndPositionWithTaskbarDpi()
    {
        var native = CreateAttachableNative();
        native.Dpi = 192;
        native.TaskbarRect = new TaskbarRect { Left = 0, Top = 0, Right = 2880, Bottom = 96 };
        var occupancy = new FakeOccupancy
        {
            Regions =
            [
                new Rectangle(0, 0, 300, 96),
                new Rectangle(1053, 0, 1335, 96),
                new Rectangle(2808, 0, 72, 96),
            ],
        };
        var service = CreateService(native, occupancy);

        Assert.IsTrue(service.AttachToTaskbar(WindowHandle, 68, 30));

        var (_, x, y, width, height) = native.Moves.Single();
        Assert.AreEqual(312, x);
        Assert.AreEqual(18, y);
        Assert.AreEqual(136, width);
        Assert.AreEqual(60, height);
    }

    [TestMethod]
    public void DetachToDesktop_RestoresTopLevelWindowAndStyles()
    {
        var native = CreateAttachableNative();
        var service = CreateService(native, CreateDefaultOccupancy());
        Assert.IsTrue(service.AttachToTaskbar(WindowHandle, 68, 30));

        Assert.IsTrue(service.DetachToDesktop(WindowHandle));
        Assert.AreEqual(nint.Zero, native.GetRealParent(WindowHandle));
        Assert.AreEqual(0L, (long)native.GetStyle(WindowHandle) & ChildStyle);
        Assert.IsFalse(service.IsHosted);
        Assert.IsFalse(service.IsTaskbarOwned);
    }

    [TestMethod]
    public void DetachToDesktop_WhenParentCannotBeRestored_ReportsFailureAndStaysOwned()
    {
        var native = CreateAttachableNative();
        var service = CreateService(native, CreateDefaultOccupancy());
        Assert.IsTrue(service.AttachToTaskbar(WindowHandle, 68, 30));

        native.SetParentFails = true;
        Assert.IsFalse(service.DetachToDesktop(WindowHandle));
        // The real parent link still points at the taskbar, so the window is still owned
        // and the snapshot stays pending; it must not be treated as a clean floating window.
        Assert.IsTrue(service.IsTaskbarOwned);
        Assert.IsTrue(service.HasPendingRestoration);
        Assert.IsTrue(service.IsTaskbarManaged);
        Assert.IsFalse(service.IsHosted);
    }

    [TestMethod]
    public void DetachToDesktop_WhenOwnerAppearsAfterAttach_ClearsToSnapshotOwner()
    {
        var native = CreateAttachableNative();
        native.Owners[WindowHandle] = nint.Zero;
        var service = CreateService(native, CreateDefaultOccupancy());
        Assert.IsTrue(service.AttachToTaskbar(WindowHandle, 68, 30));

        // WPF installing an owner after Show() must be cleared back to the snapshot (0),
        // not merely accepted as an arbitrary new owner.
        native.Owners[WindowHandle] = 99;

        Assert.IsTrue(service.DetachToDesktop(WindowHandle));
        Assert.AreEqual(nint.Zero, native.GetOwner(WindowHandle));
        Assert.AreEqual(0L, (long)native.GetStyle(WindowHandle) & ChildStyle);
    }

    [TestMethod]
    public void DetachToDesktop_NonPopupTopLevel_RestoresParentNullAndExactOwner()
    {
        const long nonPopupStyle = 0x06080000L;
        const long layeredToolWindowTopmost = 0x00080088L;
        var native = CreateAttachableNative();
        native.Styles[WindowHandle] = (nint)nonPopupStyle;
        native.ExtendedStyles[WindowHandle] = (nint)layeredToolWindowTopmost;
        native.Owners[WindowHandle] = 0xb0bc8;
        var service = CreateService(native, CreateDefaultOccupancy());

        Assert.IsTrue(service.AttachToTaskbar(WindowHandle, 68, 30));
        Assert.IsTrue(service.IsHosted);

        Assert.IsTrue(service.DetachToDesktop(WindowHandle));
        Assert.AreEqual(nint.Zero, native.GetRealParent(WindowHandle));
        Assert.AreEqual(0xb0bc8, native.GetOwner(WindowHandle));
        Assert.AreEqual((nint)nonPopupStyle, native.GetStyle(WindowHandle));
        Assert.AreEqual((nint)layeredToolWindowTopmost, native.GetExtendedStyle(WindowHandle));
        Assert.IsFalse(service.HasPendingRestoration);
    }

    [TestMethod]
    public void DetachToDesktop_PopupTopLevel_RestoresRealParentNullAndOwner()
    {
        var native = CreateAttachableNative();
        native.Owners[WindowHandle] = WindowOwner;
        var service = CreateService(native, CreateDefaultOccupancy());
        Assert.IsTrue(service.AttachToTaskbar(WindowHandle, 68, 30));

        Assert.IsTrue(service.DetachToDesktop(WindowHandle));
        // The real parent link is the desktop (normalized to 0); the owner is restored
        // separately and is not conflated with the parent.
        Assert.AreEqual(nint.Zero, native.GetRealParent(WindowHandle));
        Assert.AreEqual(WindowOwner, native.GetOwner(WindowHandle));
    }

    [TestMethod]
    public void DetachToDesktop_WhenParentRestoreKeepsFailing_StaysPendingUntilRecovered()
    {
        // Popup original.
        var popupNative = CreateAttachableNative();
        popupNative.Owners[WindowHandle] = WindowOwner;
        var popupService = CreateService(popupNative, CreateDefaultOccupancy());
        Assert.IsTrue(popupService.AttachToTaskbar(WindowHandle, 68, 30));
        popupNative.SetParentFails = true;
        Assert.IsFalse(popupService.DetachToDesktop(WindowHandle));
        Assert.IsFalse(popupService.DetachToDesktop(WindowHandle));
        Assert.IsTrue(popupService.HasPendingRestoration);
        popupNative.SetParentFails = false;
        Assert.IsTrue(popupService.DetachToDesktop(WindowHandle));
        Assert.IsFalse(popupService.HasPendingRestoration);

        // Non-popup original (matches the real widget).
        var nonPopupNative = CreateAttachableNative();
        nonPopupNative.Styles[WindowHandle] = 0x06080000;
        nonPopupNative.Owners[WindowHandle] = 0xb0bc8;
        var nonPopupService = CreateService(nonPopupNative, CreateDefaultOccupancy());
        Assert.IsTrue(nonPopupService.AttachToTaskbar(WindowHandle, 68, 30));
        nonPopupNative.SetParentFails = true;
        Assert.IsFalse(nonPopupService.DetachToDesktop(WindowHandle));
        Assert.IsFalse(nonPopupService.DetachToDesktop(WindowHandle));
        Assert.IsTrue(nonPopupService.HasPendingRestoration);
        nonPopupNative.SetParentFails = false;
        Assert.IsTrue(nonPopupService.DetachToDesktop(WindowHandle));
        Assert.IsFalse(nonPopupService.HasPendingRestoration);
    }

    [TestMethod]
    public void AttachToTaskbar_WhenRollbackAlsoFails_KeepsPendingSnapshotForRetry()
    {
        var native = CreateAttachableNative();
        native.Owners[WindowHandle] = WindowOwner;
        var service = CreateService(native, CreateDefaultOccupancy());

        // Force the initial attach to fail after styles were applied, and make the style
        // rollback (the second SetStyle call) fail as well.
        native.MoveWindowFails = true;
        native.SetStyleFailFromCall = 2;
        Assert.IsFalse(service.AttachToTaskbar(WindowHandle, 68, 30));
        Assert.IsTrue(service.HasPendingRestoration);

        // The retained snapshot lets the same entry point finish recovery later.
        native.MoveWindowFails = false;
        native.SetStyleFailFromCall = int.MaxValue;
        Assert.IsTrue(service.DetachToDesktop(WindowHandle));
        Assert.AreEqual((nint)PopupStyleValue, native.GetStyle(WindowHandle));
        Assert.AreEqual(WindowOwner, native.GetOwner(WindowHandle));
        Assert.IsFalse(service.HasPendingRestoration);
    }

    [TestMethod]
    public void DetachToDesktop_WhenOwnerRestoreFails_KeepsStateAndRetrySucceeds()
    {
        var native = CreateAttachableNative();
        native.Owners[WindowHandle] = WindowOwner;
        var service = CreateService(native, CreateDefaultOccupancy());
        Assert.IsTrue(service.AttachToTaskbar(WindowHandle, 68, 30));

        native.Owners[WindowHandle] = 88;
        native.SetOwnerFails = true;
        Assert.IsFalse(service.DetachToDesktop(WindowHandle));

        // The original snapshot is retained, so a retry can still complete recovery.
        native.SetOwnerFails = false;
        Assert.IsTrue(service.DetachToDesktop(WindowHandle));
        Assert.AreEqual(WindowOwner, native.GetOwner(WindowHandle));
        Assert.AreEqual(0L, (long)native.GetStyle(WindowHandle) & ChildStyle);
    }

    [TestMethod]
    public void Reposition_WhenNotAttached_ReturnsFailure()
    {
        var native = CreateAttachableNative();
        var service = CreateService(native, CreateDefaultOccupancy());

        Assert.AreEqual(TaskbarLayoutResult.Failure, service.Reposition(WindowHandle, 68, 30));
        Assert.IsEmpty(native.Moves);
    }

    [TestMethod]
    public void Reposition_WhenNoSafeSlot_ReturnsNoSafeSlotWithoutMoving()
    {
        var native = CreateAttachableNative();
        var occupancy = CreateDefaultOccupancy();
        var service = CreateService(native, occupancy);
        Assert.IsTrue(service.AttachToTaskbar(WindowHandle, 68, 30));
        var movesAfterAttach = native.Moves.Count;

        occupancy.Regions = [new Rectangle(0, 0, 1920, 48)];

        Assert.AreEqual(TaskbarLayoutResult.NoSafeSlot, service.Reposition(WindowHandle, 68, 30));
        Assert.HasCount(movesAfterAttach, native.Moves);
    }

    [TestMethod]
    public void Reposition_AfterDpiChange_UsesNewScale()
    {
        var native = CreateAttachableNative();
        var service = CreateService(native, CreateDefaultOccupancy());
        Assert.IsTrue(service.AttachToTaskbar(WindowHandle, 68, 30));

        native.Dpi = 120;
        Assert.AreEqual(TaskbarLayoutResult.Repositioned, service.Reposition(WindowHandle, 68, 30));

        var (_, _, y, width, height) = native.Moves[^1];
        Assert.AreEqual(5, y);
        Assert.AreEqual(85, width);
        Assert.AreEqual(38, height);
    }

    [TestMethod]
    public void RepositionIfNeeded_WhenAlreadyPlaced_ReturnsUpToDateWithoutMoving()
    {
        var native = CreateAttachableNative();
        var service = CreateService(native, CreateDefaultOccupancy());
        Assert.IsTrue(service.AttachToTaskbar(WindowHandle, 68, 30));

        Assert.AreEqual(TaskbarLayoutResult.UpToDate, service.RepositionIfNeeded(WindowHandle, 68, 30));
        Assert.HasCount(1, native.Moves);
    }

    [TestMethod]
    public void RepositionIfNeeded_WhenTaskbarIsMoving_PausesThenResumes()
    {
        var native = CreateAttachableNative();
        var service = CreateService(native, CreateDefaultOccupancy());
        Assert.IsTrue(service.AttachToTaskbar(WindowHandle, 68, 30));
        var movesAfterAttach = native.Moves.Count;

        native.TaskbarRect = new TaskbarRect { Left = 0, Top = 40, Right = 1920, Bottom = 88 };

        Assert.AreEqual(TaskbarLayoutResult.TaskbarMoving, service.RepositionIfNeeded(WindowHandle, 68, 30));
        Assert.HasCount(movesAfterAttach, native.Moves);

        // The adopted rect must let a later stable pass resume instead of skipping forever.
        var resumed = service.RepositionIfNeeded(WindowHandle, 68, 30);
        Assert.AreNotEqual(TaskbarLayoutResult.TaskbarMoving, resumed);
    }

    [TestMethod]
    public void IsHostHandleAlive_IsFalseAfterExplorerDestroysChild()
    {
        var native = CreateAttachableNative();
        var service = CreateService(native, CreateDefaultOccupancy());
        Assert.IsTrue(service.AttachToTaskbar(WindowHandle, 68, 30));

        native.AliveHandles.Remove(WindowHandle);

        Assert.IsFalse(service.IsHostHandleAlive);
        Assert.IsFalse(service.IsHosted);
        Assert.IsFalse(service.IsTaskbarOwned);
    }

    [TestMethod]
    public void HostedVisibility_UsesNativeHideShowAndRepaintOnlyWhenAttached()
    {
        var native = CreateAttachableNative();
        var service = CreateService(native, CreateDefaultOccupancy());

        // Not attached: no native visibility/repaint calls.
        Assert.IsFalse(service.HideHostedCore(WindowHandle));
        Assert.IsFalse(service.ShowHostedCore(WindowHandle));
        Assert.IsFalse(service.RefreshHostedCore(WindowHandle));
        Assert.IsEmpty(native.VisibilityCalls);
        Assert.AreEqual(0, native.RaiseAndRedrawCalls);

        Assert.IsTrue(service.AttachToTaskbar(WindowHandle, 68, 30));

        Assert.IsTrue(service.HideHostedCore(WindowHandle));
        Assert.AreEqual((WindowHandle, false), native.VisibilityCalls[^1]);

        Assert.IsTrue(service.ShowHostedCore(WindowHandle));
        Assert.AreEqual((WindowHandle, true), native.VisibilityCalls[^1]);
        Assert.IsGreaterThan(0, native.RaiseAndRedrawCalls);

        var beforeRefresh = native.RaiseAndRedrawCalls;
        Assert.IsTrue(service.RefreshHostedCore(WindowHandle));
        Assert.IsGreaterThan(beforeRefresh, native.RaiseAndRedrawCalls);
    }

    [TestMethod]
    public void DecideRecovery_WhenNotIntended_DoesNothing()
    {
        Assert.AreEqual(
            TaskbarRecoveryAction.None,
            TaskbarHostService.DecideRecovery(hostIntended: false, handleAlive: true, hosted: true));
        Assert.AreEqual(
            TaskbarRecoveryAction.None,
            TaskbarHostService.DecideRecovery(hostIntended: false, handleAlive: false, hosted: false));
    }

    [TestMethod]
    public void DecideRecovery_WhenHandleDestroyed_RecreatesWindow()
    {
        Assert.AreEqual(
            TaskbarRecoveryAction.Recreate,
            TaskbarHostService.DecideRecovery(hostIntended: true, handleAlive: false, hosted: false));
    }

    [TestMethod]
    public void DecideRecovery_WhenHandleAliveButNotHosted_Reattaches()
    {
        Assert.AreEqual(
            TaskbarRecoveryAction.Reattach,
            TaskbarHostService.DecideRecovery(hostIntended: true, handleAlive: true, hosted: false));
    }

    [TestMethod]
    public void DecideRecovery_WhenHealthy_DoesNothing()
    {
        Assert.AreEqual(
            TaskbarRecoveryAction.None,
            TaskbarHostService.DecideRecovery(hostIntended: true, handleAlive: true, hosted: true));
    }

    private static FakeTaskbarNative CreateAttachableNative()
    {
        var native = new FakeTaskbarNative();
        native.AliveHandles.Add(WindowHandle);
        native.AliveHandles.Add(TaskbarHandle);
        native.Styles[WindowHandle] = PopupStyleValue;
        native.ExtendedStyles[WindowHandle] = (nint)TopmostExtendedStyle;
        return native;
    }

    private static FakeOccupancy CreateDefaultOccupancy() => new()
    {
        Regions =
        [
            new Rectangle(0, 0, 200, 48),
            new Rectangle(702, 0, 890, 48),
            new Rectangle(1872, 0, 48, 48),
        ],
    };

    private static TaskbarHostService CreateService(FakeTaskbarNative native, FakeOccupancy occupancy) =>
        new(native, occupancy, new AppLogger(Path.Combine(Path.GetTempPath(), "CodexQuotaWidget-taskbar-tests.log")));

    private sealed class FakeOccupancy : ITaskbarOccupancyProvider
    {
        public bool Available { get; set; } = true;
        public List<Rectangle> Regions { get; set; } = new();

        public bool TryGetOccupiedRegions(
            nint taskbarHandle,
            nint excludeHandle,
            out IReadOnlyList<Rectangle> occupiedRegions)
        {
            occupiedRegions = Regions;
            return Available;
        }
    }

    private sealed class FakeTaskbarNative : ITaskbarHostNative
    {
        private readonly Dictionary<nint, nint> _parents = new();
        private readonly Dictionary<nint, TaskbarRect> _rects = new();
        private int _setStyleCalls;

        public nint TaskbarHandle { get; set; } = TaskbarHostServiceTests.TaskbarHandle;
        public bool TaskbarExists { get; set; } = true;
        public bool SetParentFails { get; set; }
        public bool SetOwnerFails { get; set; }
        public bool SetStyleFails { get; set; }
        public int SetStyleFailFromCall { get; set; } = int.MaxValue;
        public bool MoveWindowFails { get; set; }
        public uint Dpi { get; set; } = 96;
        public TaskbarRect TaskbarRect { get; set; } = new() { Left = 0, Top = 0, Right = 1920, Bottom = 48 };
        public HashSet<nint> AliveHandles { get; } = new();
        public Dictionary<nint, nint> Styles { get; } = new();
        public Dictionary<nint, nint> ExtendedStyles { get; } = new();
        public Dictionary<nint, nint> Owners { get; } = new();
        public List<(nint Handle, int X, int Y, int Width, int Height)> Moves { get; } = new();

        public nint FindTaskbar() => TaskbarExists ? TaskbarHandle : nint.Zero;

        public nint GetRealParent(nint windowHandle) =>
            _parents.TryGetValue(windowHandle, out var parent) ? parent : nint.Zero;

        public nint GetDesktopHandle() => 1;

        public nint SetParent(nint childHandle, nint parentHandle)
        {
            var previous = GetRealParent(childHandle);
            if (!SetParentFails)
            {
                _parents[childHandle] = parentHandle;
            }

            return previous;
        }

        public void SetParentDirect(nint childHandle, nint parentHandle) =>
            _parents[childHandle] = parentHandle;

        public bool IsWindow(nint windowHandle) => AliveHandles.Contains(windowHandle);

        public bool GetWindowRect(nint windowHandle, out TaskbarRect rectangle)
        {
            if (windowHandle == TaskbarHandle)
            {
                rectangle = TaskbarRect;
                return true;
            }

            if (_rects.TryGetValue(windowHandle, out rectangle))
            {
                return true;
            }

            rectangle = default;
            return false;
        }

        public uint GetDpiForWindow(nint windowHandle) => Dpi;

        public bool MoveWindow(nint windowHandle, int x, int y, int width, int height)
        {
            if (MoveWindowFails)
            {
                return false;
            }

            Moves.Add((windowHandle, x, y, width, height));
            _rects[windowHandle] = new TaskbarRect { Left = x, Top = y, Right = x + width, Bottom = y + height };
            return true;
        }

        public bool RefreshWindowFrame(nint windowHandle) => true;

        public List<(nint Handle, bool Visible)> VisibilityCalls { get; } = new();
        public int RaiseAndRedrawCalls { get; private set; }

        public bool SetWindowVisible(nint windowHandle, bool visible)
        {
            VisibilityCalls.Add((windowHandle, visible));
            return true;
        }

        public bool IsWindowVisible(nint windowHandle) => true;

        public bool RaiseAndRedrawWindow(nint windowHandle)
        {
            RaiseAndRedrawCalls++;
            return true;
        }

        public void ScreenToClient(nint windowHandle, ref TaskbarPoint point)
        {
        }

        public nint GetStyle(nint windowHandle) =>
            Styles.TryGetValue(windowHandle, out var style) ? style : nint.Zero;

        public nint GetExtendedStyle(nint windowHandle) =>
            ExtendedStyles.TryGetValue(windowHandle, out var style) ? style : nint.Zero;

        public nint SetStyle(nint windowHandle, nint style)
        {
            var previous = GetStyle(windowHandle);
            _setStyleCalls++;
            if (!SetStyleFails && _setStyleCalls < SetStyleFailFromCall)
            {
                Styles[windowHandle] = style;
            }

            return previous;
        }

        public nint SetExtendedStyle(nint windowHandle, nint extendedStyle)
        {
            var previous = GetExtendedStyle(windowHandle);
            ExtendedStyles[windowHandle] = extendedStyle;
            return previous;
        }

        public nint GetOwner(nint windowHandle) =>
            Owners.TryGetValue(windowHandle, out var owner) ? owner : nint.Zero;

        public nint SetOwner(nint windowHandle, nint ownerHandle)
        {
            var previous = GetOwner(windowHandle);
            if (!SetOwnerFails)
            {
                Owners[windowHandle] = ownerHandle;
            }

            return previous;
        }

        public int LastWin32Error => 0;
    }
}
