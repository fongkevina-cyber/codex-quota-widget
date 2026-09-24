using System.Numerics;

namespace CodexQuotaWidget.Core;

public enum DataFreshness
{
    Live,
    Stale,
    Expired,
    Offline,
}

public sealed record QuotaWindow(
    int WindowDurationMinutes,
    decimal UsedPercent,
    DateTimeOffset ResetsAt)
{
    public decimal RemainingPercent => Math.Clamp(100m - UsedPercent, 0m, 100m);
}

public sealed record QuotaSnapshot(
    QuotaWindow? FiveHour,
    QuotaWindow? Weekly,
    BigInteger? AvailableResetCount,
    DateTimeOffset RetrievedAt);

public sealed record QuotaDisplayState(
    QuotaSnapshot? Snapshot,
    DataFreshness Freshness,
    DateTimeOffset? LastSuccessfulRefresh,
    string? ErrorMessage,
    bool IsRefreshing)
{
    public static QuotaDisplayState Initial { get; } = new(
        Snapshot: null,
        Freshness: DataFreshness.Offline,
        LastSuccessfulRefresh: null,
        ErrorMessage: null,
        IsRefreshing: false);
}
