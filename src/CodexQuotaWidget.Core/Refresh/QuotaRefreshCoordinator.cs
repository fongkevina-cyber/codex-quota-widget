using System.Threading.Channels;

namespace CodexQuotaWidget.Core;

/// <summary>
/// Coordinates foreground/background refresh intervals, notifications,
/// retry backoff, and freshness transitions for a quota provider.
/// </summary>
public sealed class QuotaRefreshCoordinator : IAsyncDisposable
{
    private readonly IQuotaProvider _provider;
    private readonly QuotaRefreshOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<ScheduleSignal> _signals = Channel.CreateBounded<ScheduleSignal>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    private QuotaDisplayState _state = QuotaDisplayState.Initial;
    private DateTimeOffset _nextRefreshAt = DateTimeOffset.MaxValue;
    private Task? _loopTask;
    private int _consecutiveFailures;
    private int _immediateRefreshRequested;
    private bool _isVisible = true;
    private int _started;
    private int _disposed;

    public QuotaRefreshCoordinator(
        IQuotaProvider provider,
        QuotaRefreshOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _provider = provider;
        _options = options ?? new QuotaRefreshOptions();
        _options.Validate();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public event EventHandler<QuotaDisplayState>? StateChanged;

    public QuotaDisplayState State
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
        {
            return;
        }

        _provider.QuotaChanged += OnQuotaChanged;
        try
        {
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetime.Token);
            await RefreshCoreAsync(linkedCancellation.Token).ConfigureAwait(false);
            _loopTask = RunLoopAsync(_lifetime.Token);
        }
        catch
        {
            _provider.QuotaChanged -= OnQuotaChanged;
            Interlocked.Exchange(ref _started, 0);
            throw;
        }
    }

    public async Task RefreshNowAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        await RefreshCoreAsync(linkedCancellation.Token).ConfigureAwait(false);
        Signal(ScheduleSignal.Reschedule);
    }

    public void SetVisible(bool isVisible)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        lock (_stateLock)
        {
            if (_isVisible == isVisible)
            {
                return;
            }

            _isVisible = isVisible;
            if (_consecutiveFailures == 0)
            {
                _nextRefreshAt = _timeProvider.GetUtcNow()
                    + (isVisible ? _options.VisibleInterval : _options.HiddenInterval);
            }
        }

        Signal(ScheduleSignal.Reschedule);
    }

    public void NotifyConnectivityRestored()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        lock (_stateLock)
        {
            _consecutiveFailures = 0;
            _nextRefreshAt = _timeProvider.GetUtcNow();
        }

        Signal(ScheduleSignal.RefreshImmediately);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _provider.QuotaChanged -= OnQuotaChanged;
        _lifetime.Cancel();
        _signals.Writer.TryComplete();

        if (_loopTask is not null)
        {
            try
            {
                await _loopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
        }

        await _refreshGate.WaitAsync().ConfigureAwait(false);
        _refreshGate.Release();
        await _provider.DisposeAsync().ConfigureAwait(false);
        _refreshGate.Dispose();
        _lifetime.Dispose();
    }

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SetState(State with { IsRefreshing = true });

            try
            {
                var snapshot = await _provider.GetQuotaAsync(cancellationToken).ConfigureAwait(false);
                var now = _timeProvider.GetUtcNow();
                lock (_stateLock)
                {
                    _consecutiveFailures = 0;
                    _nextRefreshAt = now + (_isVisible
                        ? _options.VisibleInterval
                        : _options.HiddenInterval);
                }

                SetState(new QuotaDisplayState(
                    Snapshot: snapshot,
                    Freshness: DataFreshness.Live,
                    LastSuccessfulRefresh: snapshot.RetrievedAt,
                    ErrorMessage: null,
                    IsRefreshing: false));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                SetState(State with { IsRefreshing = false });
                throw;
            }
            catch (Exception exception)
            {
                var now = _timeProvider.GetUtcNow();
                QuotaDisplayState failedState;
                lock (_stateLock)
                {
                    _consecutiveFailures++;
                    _nextRefreshAt = now + _options.GetRetryDelay(_consecutiveFailures);
                    var freshness = GetFailureFreshness(_state, now);
                    failedState = _state with
                    {
                        Freshness = freshness,
                        ErrorMessage = GetSafeErrorMessage(exception),
                        IsRefreshing = false,
                    };
                }

                SetState(failedState);
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var now = _timeProvider.GetUtcNow();
            DateTimeOffset nextRefreshAt;
            DateTimeOffset? expiresAt;
            lock (_stateLock)
            {
                nextRefreshAt = _nextRefreshAt;
                expiresAt = _state.LastSuccessfulRefresh is { } lastSuccess
                            && _state.Freshness is not DataFreshness.Expired
                    ? lastSuccess + _options.ExpireAfter
                    : null;
            }

            if (nextRefreshAt <= now)
            {
                await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (expiresAt is { } expiration && expiration <= now)
            {
                MarkExpired();
                continue;
            }

            var wakeAt = expiresAt is { } pendingExpiration && pendingExpiration < nextRefreshAt
                ? pendingExpiration
                : nextRefreshAt;
            var delay = wakeAt == DateTimeOffset.MaxValue
                ? Timeout.InfiniteTimeSpan
                : wakeAt - now;

            using var iteration = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var signalTask = _signals.Reader.WaitToReadAsync(iteration.Token).AsTask();
            var delayTask = Task.Delay(delay, _timeProvider, iteration.Token);
            var completed = await Task.WhenAny(signalTask, delayTask).ConfigureAwait(false);
            iteration.Cancel();

            if (completed == signalTask && await ReadSignalAsync(signalTask).ConfigureAwait(false))
            {
                while (_signals.Reader.TryRead(out _))
                {
                }

                if (Interlocked.Exchange(ref _immediateRefreshRequested, 0) != 0)
                {
                    await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private static async Task<bool> ReadSignalAsync(Task<bool> signalTask)
    {
        try
        {
            return await signalTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private void MarkExpired()
    {
        QuotaDisplayState expired;
        lock (_stateLock)
        {
            if (_state.Snapshot is null || _state.Freshness is DataFreshness.Expired)
            {
                return;
            }

            expired = _state with
            {
                Freshness = DataFreshness.Expired,
                ErrorMessage = _state.ErrorMessage ?? "额度数据已过期。",
            };
        }

        SetState(expired);
    }

    private DataFreshness GetFailureFreshness(QuotaDisplayState state, DateTimeOffset now)
    {
        if (state.Snapshot is null || state.LastSuccessfulRefresh is null)
        {
            return DataFreshness.Offline;
        }

        return now - state.LastSuccessfulRefresh.Value >= _options.ExpireAfter
            ? DataFreshness.Expired
            : DataFreshness.Stale;
    }

    private static string GetSafeErrorMessage(Exception exception)
    {
        return exception switch
        {
            TimeoutException => "读取额度超时。",
            CodexAppServerRequestException requestException => IsAuthenticationFailure(requestException)
                ? "Codex 登录已失效。"
                : "Codex 拒绝了额度读取请求。",
            CodexAppServerProtocolException => "Codex 返回了无法识别的额度数据。",
            CodexAppServerException => "暂时无法连接 Codex。",
            _ => "暂时无法读取额度。",
        };
    }

    private static bool IsAuthenticationFailure(string message)
    {
        string[] markers =
        [
            "unauthorized",
            "authentication",
            "not authenticated",
            "not logged in",
            "sign in",
            "login",
            "401",
            "未登录",
            "登录",
            "认证",
        ];
        return markers.Any(marker => message.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsAuthenticationFailure(CodexAppServerRequestException exception) =>
        exception.Code is 401 || IsAuthenticationFailure(exception.Message);

    private void SetState(QuotaDisplayState state)
    {
        lock (_stateLock)
        {
            _state = state;
        }

        var handlers = StateChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<QuotaDisplayState> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, state);
            }
            catch
            {
                // A UI subscriber must not stop scheduling future refreshes.
            }
        }
    }

    private void OnQuotaChanged(object? sender, EventArgs eventArgs)
    {
        Signal(ScheduleSignal.RefreshImmediately);
    }

    private void Signal(ScheduleSignal signal)
    {
        if (signal is ScheduleSignal.RefreshImmediately)
        {
            Interlocked.Exchange(ref _immediateRefreshRequested, 1);
        }

        _signals.Writer.TryWrite(signal);
    }

    private enum ScheduleSignal
    {
        Reschedule,
        RefreshImmediately,
    }
}
