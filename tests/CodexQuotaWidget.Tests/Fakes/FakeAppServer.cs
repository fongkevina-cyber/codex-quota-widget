using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CodexQuotaWidget.Core;

namespace CodexQuotaWidget.Tests.Fakes;

internal sealed class FakeAppServerFactory(params Func<FakeAppServerConnection>[] connectionFactories)
    : IAppServerProcessFactory
{
    private readonly ConcurrentQueue<Func<FakeAppServerConnection>> _connectionFactories = new(connectionFactories);
    private int _startCount;

    public int StartCount => Volatile.Read(ref _startCount);

    public IAppServerConnection Start(CodexAppServerOptions options)
    {
        Interlocked.Increment(ref _startCount);
        if (!_connectionFactories.TryDequeue(out var factory))
        {
            throw new InvalidOperationException("No scripted fake app-server connection remains.");
        }

        return factory();
    }
}

internal sealed class FakeAppServerConnection : IAppServerConnection
{
    private readonly Func<FakeAppServerConnection, string, Task> _onWrite;
    private readonly ChannelLineReader _standardOutput = new();
    private readonly ChannelLineReader _standardError = new();
    private readonly CallbackTextWriter _standardInput;
    private readonly ConcurrentQueue<string> _clientMessages = new();
    private int _hasExited;
    private int _disposed;
    private int _trimCount;

    public FakeAppServerConnection(Func<FakeAppServerConnection, string, Task> onWrite)
    {
        _onWrite = onWrite;
        _standardInput = new CallbackTextWriter(ReceiveClientMessageAsync);
    }

    public TextWriter StandardInput => _standardInput;

    public TextReader StandardOutput => _standardOutput;

    public TextReader StandardError => _standardError;

    public bool HasExited => Volatile.Read(ref _hasExited) != 0;

    public IReadOnlyList<string> ClientMessages => _clientMessages.ToArray();

    public int TrimCount => Volatile.Read(ref _trimCount);

    public void PushMessage(string jsonLine)
    {
        _standardOutput.WriteLine(jsonLine);
    }

    public void Reply(long id, string resultJson)
    {
        PushMessage($$"""{"id":{{id}},"result":{{resultJson}}}""");
    }

    public void Exit()
    {
        if (Interlocked.Exchange(ref _hasExited, 1) != 0)
        {
            return;
        }

        _standardOutput.Complete();
        _standardError.Complete();
    }

    public bool TryTrimWorkingSet()
    {
        Interlocked.Increment(ref _trimCount);
        return !HasExited;
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Exit();
        }

        return ValueTask.CompletedTask;
    }

    public static Task ReplyToInitializationAsync(FakeAppServerConnection connection, JsonElement message)
    {
        var id = message.GetProperty("id").GetInt64();
        connection.Reply(id, """{"userAgent":"fake","platformFamily":"windows","platformOs":"windows"}""");
        return Task.CompletedTask;
    }

    private async Task ReceiveClientMessageAsync(string message)
    {
        _clientMessages.Enqueue(message);
        await _onWrite(this, message).ConfigureAwait(false);
    }

    private sealed class CallbackTextWriter(Func<string, Task> callback) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override Task WriteLineAsync(string? value)
        {
            return callback(value ?? string.Empty);
        }

        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return callback(buffer.ToString());
        }

        public override Task FlushAsync() => Task.CompletedTask;

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class ChannelLineReader : TextReader
    {
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
            });

        public void WriteLine(string line)
        {
            if (!_lines.Writer.TryWrite(line))
            {
                throw new InvalidOperationException("The fake output stream is closed.");
            }
        }

        public void Complete()
        {
            _lines.Writer.TryComplete();
        }

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await _lines.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }

        public override ValueTask<int> ReadAsync(
            Memory<char> buffer,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(0);
        }
    }
}

internal static class FakeAppServerScripts
{
    public static Task ReplyWithQuotaAsync(
        FakeAppServerConnection connection,
        string line)
    {
        return ReplyWithQuotaAsync(connection, line, TestData.ValidResult);
    }

    public static async Task ReplyWithQuotaAsync(
        FakeAppServerConnection connection,
        string line,
        string quotaResult)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        var method = root.GetProperty("method").GetString();
        switch (method)
        {
            case CodexAppServerQuotaProvider.InitializeMethod:
                await FakeAppServerConnection.ReplyToInitializationAsync(connection, root);
                break;
            case CodexAppServerQuotaProvider.InitializedMethod:
                break;
            case CodexAppServerQuotaProvider.ReadRateLimitsMethod:
                connection.Reply(root.GetProperty("id").GetInt64(), quotaResult);
                break;
            default:
                throw new AssertFailedException($"Unexpected client method: {method}");
        }
    }
}
