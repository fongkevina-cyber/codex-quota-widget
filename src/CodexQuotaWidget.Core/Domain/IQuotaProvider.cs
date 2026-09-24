namespace CodexQuotaWidget.Core;

/// <summary>
/// Supplies read-only Codex quota snapshots.
/// </summary>
public interface IQuotaProvider : IAsyncDisposable
{
    /// <summary>
    /// Raised when the upstream service reports that quota data changed.
    /// Consumers should fetch a new snapshot; the notification itself is not
    /// treated as an authoritative snapshot.
    /// </summary>
    event EventHandler? QuotaChanged;

    Task<QuotaSnapshot> GetQuotaAsync(CancellationToken cancellationToken = default);
}
