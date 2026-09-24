using System.Collections.Concurrent;
using CodexQuotaWidget.Core;

namespace CodexQuotaWidget.Tests;

[TestClass]
public sealed class QuotaRefreshCoordinatorTests
{
    [TestMethod]
    public void DefaultsMatchForegroundBackgroundAndExpiryRequirements()
    {
        var options = new QuotaRefreshOptions();

        Assert.AreEqual(TimeSpan.FromMinutes(1), options.VisibleInterval);
        Assert.AreEqual(TimeSpan.FromMinutes(5), options.HiddenInterval);
        Assert.AreEqual(TimeSpan.FromMinutes(15), options.ExpireAfter);
        Assert.AreEqual(TimeSpan.FromMinutes(15), options.MaximumRetryDelay);
    }

    [TestMethod]
    public void RetryBackoffDoublesAndCapsAtMaximum()
    {
        var options = new QuotaRefreshOptions
        {
            InitialRetryDelay = TimeSpan.FromSeconds(5),
            MaximumRetryDelay = TimeSpan.FromMinutes(15),
        };

        Assert.AreEqual(TimeSpan.FromSeconds(5), options.GetRetryDelay(1));
        Assert.AreEqual(TimeSpan.FromSeconds(10), options.GetRetryDelay(2));
        Assert.AreEqual(TimeSpan.FromMinutes(15), options.GetRetryDelay(20));
        Assert.AreEqual(TimeSpan.FromMinutes(15), options.GetRetryDelay(int.MaxValue));
    }

    [TestMethod]
    public async Task StartsWithImmediateRefreshAndNotificationTriggersAnotherRead()
    {
        var provider = new StubQuotaProvider(_ => Task.FromResult(CreateSnapshot()));
        await using var coordinator = new QuotaRefreshCoordinator(
            provider,
            LongRunningOptions());

        await coordinator.StartAsync();
        provider.RaiseQuotaChanged();
        coordinator.SetVisible(false); // Rescheduling must not erase the pending notification refresh.

        await WaitUntilAsync(() => provider.CallCount >= 2, TimeSpan.FromSeconds(2));
        Assert.AreEqual(DataFreshness.Live, coordinator.State.Freshness);
        Assert.IsNull(coordinator.State.ErrorMessage);
    }

    [TestMethod]
    public async Task FailureKeepsLastSnapshotThenMarksItExpired()
    {
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var firstCall = 0;
        var provider = new StubQuotaProvider(_ =>
        {
            if (Interlocked.Increment(ref firstCall) == 1)
            {
                return Task.FromResult(CreateSnapshot(timeProvider.GetUtcNow()));
            }

            throw new CodexAppServerException("offline");
        });
        var options = LongRunningOptions() with
        {
            ExpireAfter = TimeSpan.FromMinutes(15),
        };
        await using var coordinator = new QuotaRefreshCoordinator(provider, options, timeProvider);

        await coordinator.StartAsync();
        await coordinator.RefreshNowAsync();
        var staleState = coordinator.State;
        Assert.AreEqual(DataFreshness.Stale, staleState.Freshness);
        Assert.IsNotNull(staleState.Snapshot);

        timeProvider.Advance(TimeSpan.FromMinutes(16));
        await coordinator.RefreshNowAsync();
        var expiredState = coordinator.State;
        Assert.AreEqual(DataFreshness.Expired, expiredState.Freshness);
        Assert.IsNotNull(expiredState.Snapshot);
        Assert.IsNotNull(expiredState.LastSuccessfulRefresh);
        Assert.IsFalse(expiredState.IsRefreshing);
    }

    [TestMethod]
    public async Task InitialFailureIsOfflineAndDoesNotInventSnapshot()
    {
        var provider = new StubQuotaProvider(_ => throw new TimeoutException("secret detail"));
        await using var coordinator = new QuotaRefreshCoordinator(provider, LongRunningOptions());

        await coordinator.StartAsync();

        Assert.AreEqual(DataFreshness.Offline, coordinator.State.Freshness);
        Assert.IsNull(coordinator.State.Snapshot);
        Assert.AreEqual("读取额度超时。", coordinator.State.ErrorMessage);
    }

    [TestMethod]
    public async Task ServerErrorsAreMappedToFixedMessagesWithoutLeakingDetails()
    {
        var authenticationProvider = new StubQuotaProvider(_ =>
            throw new CodexAppServerRequestException("private backend detail", 401));
        await using var authenticationCoordinator = new QuotaRefreshCoordinator(
            authenticationProvider,
            LongRunningOptions());

        await authenticationCoordinator.StartAsync();

        var authenticationError = authenticationCoordinator.State.ErrorMessage;
        Assert.AreEqual("Codex 登录已失效。", authenticationError);
        Assert.DoesNotContain("private backend detail", authenticationError!);

        var rejectedProvider = new StubQuotaProvider(_ =>
            throw new CodexAppServerRequestException("backend detail should stay private", 500));
        await using var rejectedCoordinator = new QuotaRefreshCoordinator(
            rejectedProvider,
            LongRunningOptions());

        await rejectedCoordinator.StartAsync();

        var rejectedError = rejectedCoordinator.State.ErrorMessage;
        Assert.AreEqual("Codex 拒绝了额度读取请求。", rejectedError);
        Assert.DoesNotContain("backend detail", rejectedError!);
    }

    [TestMethod]
    public async Task ManualRefreshPreservesLastSuccessWhenReadFails()
    {
        var outcomes = new ConcurrentQueue<Func<Task<QuotaSnapshot>>>(
        [
            () => Task.FromResult(CreateSnapshot()),
            () => throw new InvalidOperationException("boom"),
        ]);
        var provider = new StubQuotaProvider(_ => outcomes.TryDequeue(out var outcome)
            ? outcome()
            : Task.FromResult(CreateSnapshot()));
        await using var coordinator = new QuotaRefreshCoordinator(provider, LongRunningOptions());

        await coordinator.StartAsync();
        var first = coordinator.State.Snapshot;
        await coordinator.RefreshNowAsync();

        Assert.AreSame(first, coordinator.State.Snapshot);
        Assert.AreEqual(DataFreshness.Stale, coordinator.State.Freshness);
        Assert.AreEqual("暂时无法读取额度。", coordinator.State.ErrorMessage);
    }

    [TestMethod]
    public async Task ConnectivityRestoreTriggersImmediateRetry()
    {
        var provider = new StubQuotaProvider(_ => throw new CodexAppServerException("offline"));
        await using var coordinator = new QuotaRefreshCoordinator(provider, LongRunningOptions());
        await coordinator.StartAsync();
        var callsAfterStart = provider.CallCount;

        coordinator.NotifyConnectivityRestored();

        await WaitUntilAsync(() => provider.CallCount > callsAfterStart, TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task DisposingCoordinatorDisposesOwnedProvider()
    {
        var provider = new StubQuotaProvider(_ => Task.FromResult(CreateSnapshot()));
        var coordinator = new QuotaRefreshCoordinator(provider, LongRunningOptions());
        await coordinator.StartAsync();

        await coordinator.DisposeAsync();

        Assert.IsTrue(provider.IsDisposed);
    }

    [TestMethod]
    public async Task DisposeCancelsAnInFlightInitialRefresh()
    {
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new StubQuotaProvider(async cancellationToken =>
        {
            requestStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return CreateSnapshot();
        });
        var coordinator = new QuotaRefreshCoordinator(provider, LongRunningOptions());

        var startTask = coordinator.StartAsync();
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => startTask);
        Assert.IsTrue(provider.IsDisposed);
    }

    [TestMethod]
    public async Task DisposeCancelsAnInFlightManualRefresh()
    {
        var call = 0;
        var manualRequestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new StubQuotaProvider(async cancellationToken =>
        {
            if (Interlocked.Increment(ref call) == 1)
            {
                return CreateSnapshot();
            }

            manualRequestStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return CreateSnapshot();
        });
        var coordinator = new QuotaRefreshCoordinator(provider, LongRunningOptions());
        await coordinator.StartAsync();

        var refreshTask = coordinator.RefreshNowAsync();
        await manualRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => refreshTask);
        Assert.IsTrue(provider.IsDisposed);
    }

    private static QuotaRefreshOptions LongRunningOptions()
    {
        return new QuotaRefreshOptions
        {
            VisibleInterval = TimeSpan.FromHours(1),
            HiddenInterval = TimeSpan.FromHours(2),
            ExpireAfter = TimeSpan.FromHours(3),
            InitialRetryDelay = TimeSpan.FromHours(1),
            MaximumRetryDelay = TimeSpan.FromHours(2),
        };
    }

    private static QuotaSnapshot CreateSnapshot(DateTimeOffset? retrievedAt = null)
    {
        var now = retrievedAt ?? DateTimeOffset.UtcNow;
        return new QuotaSnapshot(
            new QuotaWindow(300, 3, now.AddHours(1)),
            new QuotaWindow(10_080, 2, now.AddDays(7)),
            1,
            now);
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset initialUtc) : TimeProvider
    {
        private long _utcTicks = initialUtc.UtcTicks;

        public override DateTimeOffset GetUtcNow() =>
            new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);

        public void Advance(TimeSpan duration) => Interlocked.Add(ref _utcTicks, duration.Ticks);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                Assert.Fail("Condition was not reached before the test timeout.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class StubQuotaProvider(Func<CancellationToken, Task<QuotaSnapshot>> getQuota)
        : IQuotaProvider
    {
        private int _callCount;

        public event EventHandler? QuotaChanged;

        public int CallCount => Volatile.Read(ref _callCount);

        public bool IsDisposed { get; private set; }

        public Task<QuotaSnapshot> GetQuotaAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            return getQuota(cancellationToken);
        }

        public void RaiseQuotaChanged()
        {
            QuotaChanged?.Invoke(this, EventArgs.Empty);
        }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
