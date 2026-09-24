using System.Text.Json;
using CodexQuotaWidget.Core;
using CodexQuotaWidget.Tests.Fakes;

namespace CodexQuotaWidget.Tests;

[TestClass]
public sealed class CodexAppServerQuotaProviderTests
{
    [TestMethod]
    public async Task SendsOnlyStableReadOnlyProtocolMessages()
    {
        var connection = new FakeAppServerConnection(FakeAppServerScripts.ReplyWithQuotaAsync);
        var factory = new FakeAppServerFactory(() => connection);
        await using var provider = CreateProvider(factory);

        var snapshot = await provider.GetQuotaAsync();

        Assert.IsNotNull(snapshot.FiveHour);
        Assert.IsNotNull(snapshot.Weekly);
        var messages = connection.ClientMessages.Select(ParseMessage).ToArray();
        CollectionAssert.AreEqual(
            new[]
            {
                CodexAppServerQuotaProvider.InitializeMethod,
                CodexAppServerQuotaProvider.InitializedMethod,
                CodexAppServerQuotaProvider.ReadRateLimitsMethod,
            },
            messages.Select(message => message.GetProperty("method").GetString()).ToArray());

        var initializeParams = messages[0].GetProperty("params");
        Assert.AreEqual(JsonValueKind.Null, initializeParams.GetProperty("capabilities").ValueKind);
        Assert.AreEqual("codex_quota_widget", initializeParams.GetProperty("clientInfo").GetProperty("name").GetString());
        Assert.IsFalse(messages[2].TryGetProperty("params", out _));
        Assert.IsTrue(messages.All(message =>
            !message.GetProperty("method").GetString()!.Contains("Reset", StringComparison.OrdinalIgnoreCase)
            && !message.GetProperty("method").GetString()!.Contains("consume", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task LowMemoryModeClosesSuccessfulSessionAndStartsFreshForNextPoll()
    {
        var firstConnection = new FakeAppServerConnection(FakeAppServerScripts.ReplyWithQuotaAsync);
        var secondConnection = new FakeAppServerConnection(FakeAppServerScripts.ReplyWithQuotaAsync);
        var factory = new FakeAppServerFactory(() => firstConnection, () => secondConnection);
        var options = new CodexAppServerOptions("fake-codex.exe")
        {
            KeepAppServerAlive = false,
            RequestTimeout = TimeSpan.FromSeconds(2),
        };
        await using var provider = new CodexAppServerQuotaProvider(options, factory);

        await provider.GetQuotaAsync();
        Assert.IsTrue(firstConnection.HasExited);

        await provider.GetQuotaAsync();

        Assert.IsTrue(secondConnection.HasExited);
        Assert.AreEqual(2, factory.StartCount);
    }

    [TestMethod]
    public async Task PersistentLowMemoryModeTrimsSessionWithoutRestartingIt()
    {
        var connection = new FakeAppServerConnection(FakeAppServerScripts.ReplyWithQuotaAsync);
        var factory = new FakeAppServerFactory(() => connection);
        var options = new CodexAppServerOptions("fake-codex.exe")
        {
            TrimAppServerWorkingSet = true,
            RequestTimeout = TimeSpan.FromSeconds(2),
        };
        await using var provider = new CodexAppServerQuotaProvider(options, factory);

        await provider.GetQuotaAsync();
        await provider.GetQuotaAsync();

        Assert.IsFalse(connection.HasExited);
        Assert.AreEqual(2, connection.TrimCount);
        Assert.AreEqual(1, factory.StartCount);
    }

    [TestMethod]
    public async Task RateLimitsUpdatedNotificationRaisesQuotaChanged()
    {
        var connection = new FakeAppServerConnection(FakeAppServerScripts.ReplyWithQuotaAsync);
        var factory = new FakeAppServerFactory(() => connection);
        await using var provider = CreateProvider(factory);
        var notification = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.QuotaChanged += (_, _) => notification.TrySetResult();

        await provider.GetQuotaAsync();
        connection.PushMessage("""
            {"method":"account/rateLimits/updated","params":{"rateLimits":{}}}
            """);

        await notification.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task MalformedJsonlFailsTheRequestAndNextCallStartsANewProcess()
    {
        var malformedConnection = new FakeAppServerConnection(async (connection, line) =>
        {
            using var document = JsonDocument.Parse(line);
            var message = document.RootElement;
            var method = message.GetProperty("method").GetString();
            if (method == CodexAppServerQuotaProvider.InitializeMethod)
            {
                await FakeAppServerConnection.ReplyToInitializationAsync(connection, message);
            }
            else if (method == CodexAppServerQuotaProvider.ReadRateLimitsMethod)
            {
                connection.PushMessage("{this is not JSON");
            }
        });
        var healthyConnection = new FakeAppServerConnection(FakeAppServerScripts.ReplyWithQuotaAsync);
        var factory = new FakeAppServerFactory(() => malformedConnection, () => healthyConnection);
        await using var provider = CreateProvider(factory);

        await Assert.ThrowsExactlyAsync<CodexAppServerProtocolException>(
            () => provider.GetQuotaAsync());
        var snapshot = await provider.GetQuotaAsync();

        Assert.IsNotNull(snapshot.FiveHour);
        Assert.AreEqual(2, factory.StartCount);
    }

    [TestMethod]
    public async Task TimedOutReadIsDiscardedAndNextCallReconnects()
    {
        var silentConnection = new FakeAppServerConnection(async (connection, line) =>
        {
            using var document = JsonDocument.Parse(line);
            var message = document.RootElement;
            if (message.GetProperty("method").GetString() == CodexAppServerQuotaProvider.InitializeMethod)
            {
                await FakeAppServerConnection.ReplyToInitializationAsync(connection, message);
            }
        });
        var healthyConnection = new FakeAppServerConnection(FakeAppServerScripts.ReplyWithQuotaAsync);
        var factory = new FakeAppServerFactory(() => silentConnection, () => healthyConnection);
        await using var provider = CreateProvider(factory, TimeSpan.FromMilliseconds(80));

        await Assert.ThrowsExactlyAsync<TimeoutException>(() => provider.GetQuotaAsync());
        var snapshot = await provider.GetQuotaAsync();

        Assert.IsNotNull(snapshot.Weekly);
        Assert.AreEqual(2, factory.StartCount);
    }

    [TestMethod]
    public async Task ExitedProcessFailsPendingReadAndNextCallReconnects()
    {
        var exitedConnection = new FakeAppServerConnection(async (connection, line) =>
        {
            using var document = JsonDocument.Parse(line);
            var message = document.RootElement;
            var method = message.GetProperty("method").GetString();
            if (method == CodexAppServerQuotaProvider.InitializeMethod)
            {
                await FakeAppServerConnection.ReplyToInitializationAsync(connection, message);
            }
            else if (method == CodexAppServerQuotaProvider.ReadRateLimitsMethod)
            {
                connection.Exit();
            }
        });
        var healthyConnection = new FakeAppServerConnection(FakeAppServerScripts.ReplyWithQuotaAsync);
        var factory = new FakeAppServerFactory(() => exitedConnection, () => healthyConnection);
        await using var provider = CreateProvider(factory);

        await Assert.ThrowsExactlyAsync<CodexAppServerException>(() => provider.GetQuotaAsync());
        var snapshot = await provider.GetQuotaAsync();

        Assert.AreEqual(97m, snapshot.FiveHour?.RemainingPercent);
        Assert.AreEqual(2, factory.StartCount);
    }

    [TestMethod]
    public async Task MissingResultIsReportedAsProtocolFailure()
    {
        var connection = new FakeAppServerConnection(async (server, line) =>
        {
            using var document = JsonDocument.Parse(line);
            var message = document.RootElement;
            var method = message.GetProperty("method").GetString();
            if (method == CodexAppServerQuotaProvider.InitializeMethod)
            {
                await FakeAppServerConnection.ReplyToInitializationAsync(server, message);
            }
            else if (method == CodexAppServerQuotaProvider.ReadRateLimitsMethod)
            {
                server.PushMessage($$"""{"id":{{message.GetProperty("id").GetInt64()}}}""");
            }
        });
        var factory = new FakeAppServerFactory(() => connection);
        await using var provider = CreateProvider(factory);

        await Assert.ThrowsExactlyAsync<CodexAppServerProtocolException>(() => provider.GetQuotaAsync());
    }

    private static CodexAppServerQuotaProvider CreateProvider(
        IAppServerProcessFactory factory,
        TimeSpan? timeout = null)
    {
        var options = new CodexAppServerOptions("fake-codex.exe")
        {
            RequestTimeout = timeout ?? TimeSpan.FromSeconds(2),
        };
        return new CodexAppServerQuotaProvider(options, factory);
    }

    private static JsonElement ParseMessage(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
