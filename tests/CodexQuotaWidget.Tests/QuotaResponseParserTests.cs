using System.Numerics;
using System.Text.Json;
using CodexQuotaWidget.Core;

namespace CodexQuotaWidget.Tests;

[TestClass]
public sealed class QuotaResponseParserTests
{
    [TestMethod]
    public void PrefersCodexBucketOverBackwardCompatibleBucket()
    {
        const string json = """
            {
              "rateLimits": {
                "primary": { "usedPercent": 91, "windowDurationMins": 300, "resetsAt": 1000 },
                "secondary": { "usedPercent": 92, "windowDurationMins": 10080, "resetsAt": 2000 }
              },
              "rateLimitsByLimitId": {
                "codex_other": {
                  "primary": { "usedPercent": 81, "windowDurationMins": 300, "resetsAt": 1000 }
                },
                "codex": {
                  "primary": { "usedPercent": 11, "windowDurationMins": 300, "resetsAt": 3000 },
                  "secondary": { "usedPercent": 12, "windowDurationMins": 10080, "resetsAt": 4000 }
                }
              }
            }
            """;

        var snapshot = TestData.Parse(json);

        Assert.AreEqual(11m, snapshot.FiveHour?.UsedPercent);
        Assert.AreEqual(12m, snapshot.Weekly?.UsedPercent);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(3000), snapshot.FiveHour?.ResetsAt);
        Assert.AreEqual(TestData.RetrievedAt, snapshot.RetrievedAt);
    }

    [TestMethod]
    public void FallsBackAndIdentifiesWindowsWithoutDependingOnPrimaryOrder()
    {
        const string json = """
            {
              "rateLimits": {
                "primary": { "usedPercent": 22, "windowDurationMins": 10080, "resetsAt": 2000 },
                "secondary": { "usedPercent": 33, "windowDurationMins": 300, "resetsAt": 1000 }
              }
            }
            """;

        var snapshot = TestData.Parse(json);

        Assert.AreEqual(33m, snapshot.FiveHour?.UsedPercent);
        Assert.AreEqual(22m, snapshot.Weekly?.UsedPercent);
        Assert.AreEqual(300, snapshot.FiveHour?.WindowDurationMinutes);
        Assert.AreEqual(10_080, snapshot.Weekly?.WindowDurationMinutes);
    }

    [TestMethod]
    public void RemainingPercentageIsClampedButRetainsRawUsage()
    {
        var underflow = new QuotaWindow(300, -5.25m, TestData.RetrievedAt);
        var normal = new QuotaWindow(300, 2.75m, TestData.RetrievedAt);
        var overflow = new QuotaWindow(300, 108.5m, TestData.RetrievedAt);

        Assert.AreEqual(-5.25m, underflow.UsedPercent);
        Assert.AreEqual(100m, underflow.RemainingPercent);
        Assert.AreEqual(97.25m, normal.RemainingPercent);
        Assert.AreEqual(0m, overflow.RemainingPercent);
    }

    [TestMethod]
    public void AvailableCountSupportsValuesLargerThanInt64()
    {
        const string rawCount = "1234567890123456789012345678901234567890";
        var snapshot = TestData.Parse($$"""
            {
              "rateLimits": null,
              "rateLimitResetCredits": {
                "availableCount": {{rawCount}},
                "credits": []
              }
            }
            """);

        Assert.AreEqual(BigInteger.Parse(rawCount), snapshot.AvailableResetCount);
    }

    [TestMethod]
    public void ZeroAvailableCountIsPreserved()
    {
        var snapshot = TestData.Parse("""
            {
              "rateLimitResetCredits": {
                "availableCount": 0,
                "credits": [{ "id": "not-authoritative" }]
              }
            }
            """);

        Assert.AreEqual(BigInteger.Zero, snapshot.AvailableResetCount);
    }

    [TestMethod]
    public void NullOrMissingCountDoesNotInferFromCreditRows()
    {
        var explicitNull = TestData.Parse("""
            {
              "rateLimitResetCredits": {
                "availableCount": null,
                "credits": [{ "id": "one" }, { "id": "two" }]
              }
            }
            """);
        var missing = TestData.Parse("""
            {
              "rateLimitResetCredits": {
                "credits": [{ "id": "one" }]
              }
            }
            """);

        Assert.IsNull(explicitNull.AvailableResetCount);
        Assert.IsNull(missing.AvailableResetCount);
    }

    [TestMethod]
    public void NegativeAvailableCountIsRejectedAsInvalid()
    {
        var snapshot = TestData.Parse("""
            {
              "rateLimitResetCredits": {
                "availableCount": -1,
                "credits": []
              }
            }
            """);

        Assert.IsNull(snapshot.AvailableResetCount);
    }

    [TestMethod]
    public void MissingOrMalformedWindowFieldsDoNotInventQuotaWindows()
    {
        var snapshot = TestData.Parse("""
            {
              "rateLimits": {
                "primary": { "windowDurationMins": 300, "resetsAt": 1000 },
                "secondary": { "usedPercent": 12, "windowDurationMins": 10080 }
              }
            }
            """);

        Assert.IsNull(snapshot.FiveHour);
        Assert.IsNull(snapshot.Weekly);
    }

    [TestMethod]
    public void ResetUnixTimestampIsInterpretedAsAnAbsoluteInstant()
    {
        var snapshot = TestData.Parse(TestData.ValidResult);
        var expected = DateTimeOffset.FromUnixTimeSeconds(1783681200);

        Assert.AreEqual(expected, snapshot.FiveHour?.ResetsAt);
        Assert.AreEqual(expected.UtcDateTime, snapshot.FiveHour?.ResetsAt.UtcDateTime);
    }

    [TestMethod]
    public void NonObjectResultIsAProtocolError()
    {
        using var document = JsonDocument.Parse("[]");

        Assert.ThrowsExactly<CodexAppServerProtocolException>(() =>
            QuotaResponseParser.Parse(document.RootElement, TestData.RetrievedAt));
    }
}
