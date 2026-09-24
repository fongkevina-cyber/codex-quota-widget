using System.Text.Json;
using CodexQuotaWidget.Core;

namespace CodexQuotaWidget.Tests;

internal static class TestData
{
    public static readonly DateTimeOffset RetrievedAt = new(2026, 7, 10, 8, 0, 0, TimeSpan.Zero);

    public const string ValidResult = """
        {
          "rateLimits": {
            "limitId": "codex",
            "primary": {
              "usedPercent": 3,
              "windowDurationMins": 300,
              "resetsAt": 1783681200
            },
            "secondary": {
              "usedPercent": 2,
              "windowDurationMins": 10080,
              "resetsAt": 1784246400
            }
          },
          "rateLimitResetCredits": {
            "availableCount": 1,
            "credits": null
          }
        }
        """;

    public static QuotaSnapshot Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return QuotaResponseParser.Parse(document.RootElement, RetrievedAt);
    }
}
