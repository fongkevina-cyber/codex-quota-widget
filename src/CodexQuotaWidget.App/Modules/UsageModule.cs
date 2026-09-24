using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using CodexQuotaWidget.Core;

namespace CodexQuotaWidget.App.Modules;

public enum UsageStatus
{
    Loading,
    Live,
    Stale,
    Expired,
    Offline,
    AuthenticationRequired
}

/// <summary>
/// Presentation adapter for the quota module. The window only consumes this module,
/// so a future task module can be added without changing the Codex protocol client.
/// </summary>
public sealed class UsageModule : IWidgetModule, INotifyPropertyChanged, IAsyncDisposable
{
    private readonly QuotaRefreshCoordinator _coordinator;
    private readonly Dispatcher _dispatcher;
    private QuotaDisplayState _state = QuotaDisplayState.Initial;
    private bool _started;
    private bool _disposed;

    public UsageModule(QuotaRefreshCoordinator coordinator)
    {
        _coordinator = coordinator;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _coordinator.StateChanged += OnStateChanged;
    }

    public string Id => "usage";

    public string DisplayName => "使用量";

    public UsageStatus StatusKind
    {
        get
        {
            if (_state.IsRefreshing && _state.Snapshot is null)
            {
                return UsageStatus.Loading;
            }

            if (IsAuthenticationError(_state.ErrorMessage))
            {
                return UsageStatus.AuthenticationRequired;
            }

            return _state.Freshness switch
            {
                DataFreshness.Live => UsageStatus.Live,
                DataFreshness.Stale => UsageStatus.Stale,
                DataFreshness.Expired => UsageStatus.Expired,
                _ => _state.ErrorMessage is null ? UsageStatus.Loading : UsageStatus.Offline
            };
        }
    }

    public string StatusText
    {
        get
        {
            var updated = QuotaTextFormatter.FormatUpdatedAt(
                _state.LastSuccessfulRefresh,
                DateTimeOffset.Now);
            return StatusKind switch
            {
                UsageStatus.Loading => "正在读取额度…",
                UsageStatus.Live when _state.IsRefreshing => $"刷新中 · {updated}",
                UsageStatus.Live => $"实时 · {updated}",
                UsageStatus.Stale => $"连接波动 · {updated}",
                UsageStatus.Expired => $"数据已过期 · {updated}",
                UsageStatus.AuthenticationRequired => "请先在 Codex 中登录",
                UsageStatus.Offline when _state.LastSuccessfulRefresh is not null => $"离线 · {updated}",
                _ => "暂时无法读取额度"
            };
        }
    }

    public string StatusToolTip => StatusKind switch
    {
        UsageStatus.Live => "额度数据来自本机 Codex，显示的是最近一次只读查询结果。",
        UsageStatus.Stale => "刷新暂时失败，当前保留最近一次成功读取的数据。",
        UsageStatus.Expired => "最近一次成功读取已超过 15 分钟，请检查网络或 Codex 登录状态。",
        UsageStatus.AuthenticationRequired => "打开 Codex 并完成登录后，本组件会自动再次读取。",
        UsageStatus.Offline => "当前无法连接本机 Codex；恢复网络或唤醒电脑后会自动重试。",
        _ => "正在通过本机 Codex 读取额度。"
    };

    public double WeeklyRemainingPercent => ToDouble(_state.Snapshot?.Weekly?.RemainingPercent);

    public string WeeklyRemainingText => QuotaTextFormatter.FormatRemaining(_state.Snapshot?.Weekly);

    public string WeeklyRemainingAutomationText => WeeklyRemainingText == "—"
        ? "每周剩余额度暂不可用"
        : $"每周剩余额度 {WeeklyRemainingText}";

    public string WeeklyResetText => QuotaTextFormatter.FormatReset(
        _state.Snapshot?.Weekly,
        DateTimeOffset.Now);

    public string UpdatedTimeText => QuotaTextFormatter.FormatUpdatedClock(
        _state.LastSuccessfulRefresh);

    public event PropertyChangedEventHandler? PropertyChanged;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            return;
        }

        _started = true;
        await _coordinator.StartAsync(cancellationToken);
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _coordinator.RefreshNowAsync(cancellationToken);
    }

    public void SetVisible(bool visible)
    {
        if (!_disposed)
        {
            _coordinator.SetVisible(visible);
        }
    }

    public void NotifyConnectivityRestored()
    {
        if (!_disposed)
        {
            _coordinator.NotifyConnectivityRestored();
        }
    }

    private void OnStateChanged(object? sender, QuotaDisplayState state)
    {
        if (_dispatcher.CheckAccess())
        {
            ApplyState(state);
        }
        else
        {
            _dispatcher.BeginInvoke(() => ApplyState(state));
        }
    }

    private void ApplyState(QuotaDisplayState state)
    {
        if (_disposed)
        {
            return;
        }

        _state = state;
        OnPropertyChanged(string.Empty);
    }

    private static double ToDouble(decimal? value) => value is null ? 0d : decimal.ToDouble(value.Value);

    private static bool IsAuthenticationError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var markers = new[]
        {
            "unauthorized", "authentication", "not authenticated", "not logged in",
            "sign in", "login", "401", "未登录", "登录", "认证"
        };
        return markers.Any(marker => message.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _coordinator.StateChanged -= OnStateChanged;
        await _coordinator.DisposeAsync().ConfigureAwait(false);
    }
}
