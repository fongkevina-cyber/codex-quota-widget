namespace CodexQuotaWidget.Core;

public sealed record QuotaRefreshOptions
{
    public TimeSpan VisibleInterval { get; init; } = TimeSpan.FromMinutes(1);

    public TimeSpan HiddenInterval { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan ExpireAfter { get; init; } = TimeSpan.FromMinutes(15);

    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan MaximumRetryDelay { get; init; } = TimeSpan.FromMinutes(15);

    internal void Validate()
    {
        ValidatePositive(VisibleInterval, nameof(VisibleInterval));
        ValidatePositive(HiddenInterval, nameof(HiddenInterval));
        ValidatePositive(ExpireAfter, nameof(ExpireAfter));
        ValidatePositive(InitialRetryDelay, nameof(InitialRetryDelay));
        ValidatePositive(MaximumRetryDelay, nameof(MaximumRetryDelay));

        if (MaximumRetryDelay < InitialRetryDelay)
        {
            throw new ArgumentException("Maximum retry delay cannot be shorter than the initial retry delay.");
        }
    }

    internal TimeSpan GetRetryDelay(int consecutiveFailures)
    {
        if (consecutiveFailures <= 1)
        {
            return InitialRetryDelay;
        }

        var multiplier = Math.Pow(2, Math.Min(consecutiveFailures - 1, 30));
        var delayTicks = InitialRetryDelay.Ticks * multiplier;
        if (double.IsInfinity(delayTicks) || delayTicks >= MaximumRetryDelay.Ticks)
        {
            return MaximumRetryDelay;
        }

        return TimeSpan.FromTicks((long)delayTicks);
    }

    private static void ValidatePositive(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Refresh durations must be positive.");
        }
    }
}
