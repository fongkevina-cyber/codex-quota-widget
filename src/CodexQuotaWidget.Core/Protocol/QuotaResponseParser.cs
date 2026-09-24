using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace CodexQuotaWidget.Core;

internal static class QuotaResponseParser
{
    internal const int FiveHourMinutes = 300;
    internal const int WeeklyMinutes = 10_080;

    public static QuotaSnapshot Parse(JsonElement result, DateTimeOffset retrievedAt)
    {
        if (result.ValueKind is not JsonValueKind.Object)
        {
            throw new CodexAppServerProtocolException("The rate-limit response result must be a JSON object.");
        }

        JsonElement bucket = default;
        var hasBucket = false;

        if (result.TryGetProperty("rateLimitsByLimitId", out var buckets)
            && buckets.ValueKind is JsonValueKind.Object
            && buckets.TryGetProperty("codex", out var codexBucket)
            && codexBucket.ValueKind is JsonValueKind.Object)
        {
            bucket = codexBucket;
            hasBucket = true;
        }
        else if (result.TryGetProperty("rateLimits", out var fallbackBucket)
                 && fallbackBucket.ValueKind is JsonValueKind.Object)
        {
            bucket = fallbackBucket;
            hasBucket = true;
        }

        QuotaWindow? fiveHour = null;
        QuotaWindow? weekly = null;

        if (hasBucket)
        {
            foreach (var propertyName in new[] { "primary", "secondary" })
            {
                if (!bucket.TryGetProperty(propertyName, out var windowElement)
                    || !TryParseWindow(windowElement, out var window))
                {
                    continue;
                }

                if (window.WindowDurationMinutes == FiveHourMinutes && fiveHour is null)
                {
                    fiveHour = window;
                }
                else if (window.WindowDurationMinutes == WeeklyMinutes && weekly is null)
                {
                    weekly = window;
                }
            }
        }

        var availableResetCount = ParseAvailableResetCount(result);
        return new QuotaSnapshot(fiveHour, weekly, availableResetCount, retrievedAt);
    }

    private static bool TryParseWindow(JsonElement element, out QuotaWindow window)
    {
        window = null!;
        if (element.ValueKind is not JsonValueKind.Object
            || !element.TryGetProperty("windowDurationMins", out var durationElement)
            || !durationElement.TryGetInt32(out var duration)
            || (duration != FiveHourMinutes && duration != WeeklyMinutes)
            || !element.TryGetProperty("usedPercent", out var usedElement)
            || !usedElement.TryGetDecimal(out var usedPercent)
            || !element.TryGetProperty("resetsAt", out var resetsElement)
            || !resetsElement.TryGetInt64(out var resetsAtSeconds))
        {
            return false;
        }

        try
        {
            window = new QuotaWindow(
                duration,
                usedPercent,
                DateTimeOffset.FromUnixTimeSeconds(resetsAtSeconds));
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static BigInteger? ParseAvailableResetCount(JsonElement result)
    {
        if (!result.TryGetProperty("rateLimitResetCredits", out var credits)
            || credits.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            || credits.ValueKind is not JsonValueKind.Object
            || !credits.TryGetProperty("availableCount", out var count)
            || count.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (count.ValueKind is not JsonValueKind.Number
            || !BigInteger.TryParse(
                count.GetRawText(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var value))
        {
            return null;
        }

        return value.Sign < 0 ? null : value;
    }
}
