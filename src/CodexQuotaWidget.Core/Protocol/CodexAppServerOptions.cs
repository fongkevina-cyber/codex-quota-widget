namespace CodexQuotaWidget.Core;

public sealed record CodexAppServerOptions
{
    public CodexAppServerOptions(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ExecutablePath = executablePath;
    }

    public string ExecutablePath { get; }

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Keeps the Codex process connected between reads. Disable for small always-on
    /// clients that prefer lower idle memory over push notifications between polls.
    /// </summary>
    public bool KeepAppServerAlive { get; init; } = true;

    /// <summary>
    /// Returns idle app-server pages to Windows after each successful read while
    /// retaining the protocol connection and update notifications.
    /// </summary>
    public bool TrimAppServerWorkingSet { get; init; }

    public string ClientName { get; init; } = "codex_quota_widget";

    public string ClientTitle { get; init; } = "Codex Quota Widget";

    public string ClientVersion { get; init; } = "1.0.0";

    internal void Validate()
    {
        if (RequestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout), "Request timeout must be positive.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(ClientName);
        ArgumentException.ThrowIfNullOrWhiteSpace(ClientTitle);
        ArgumentException.ThrowIfNullOrWhiteSpace(ClientVersion);
    }
}
