using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexQuotaWidget.Core;

/// <summary>
/// Read-only quota client for the Codex app-server JSONL stdio transport.
/// </summary>
public sealed class CodexAppServerQuotaProvider : IQuotaProvider
{
    internal const string InitializeMethod = "initialize";
    internal const string InitializedMethod = "initialized";
    internal const string ReadRateLimitsMethod = "account/rateLimits/read";
    internal const string RateLimitsUpdatedMethod = "account/rateLimits/updated";

    private readonly CodexAppServerOptions _options;
    private readonly IAppServerProcessFactory _processFactory;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private AppServerSession? _session;
    private long _nextRequestId;
    private int _disposed;

    public CodexAppServerQuotaProvider(CodexAppServerOptions options)
        : this(options, new AppServerProcessFactory(), TimeProvider.System)
    {
    }

    internal CodexAppServerQuotaProvider(
        CodexAppServerOptions options,
        IAppServerProcessFactory processFactory,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(processFactory);
        options.Validate();
        _options = options;
        _processFactory = processFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public event EventHandler? QuotaChanged;

    public async Task<QuotaSnapshot> GetQuotaAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var session = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var result = await SendRequestAsync(
                    session,
                    ReadRateLimitsMethod,
                    parameters: null,
                    cancellationToken)
                .ConfigureAwait(false);
            var snapshot = QuotaResponseParser.Parse(result, _timeProvider.GetUtcNow());
            if (!_options.KeepAppServerAlive)
            {
                await InvalidateSessionAsync(session).ConfigureAwait(false);
            }
            else if (_options.TrimAppServerWorkingSet)
            {
                session.Connection.TryTrimWorkingSet();
            }

            return snapshot;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            await InvalidateSessionAsync(session).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _connectionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var session = _session;
            _session = null;
            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _connectionGate.Release();
            _connectionGate.Dispose();
            _writeGate.Dispose();
        }
    }

    private async Task<AppServerSession> EnsureSessionAsync(CancellationToken cancellationToken)
    {
        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            if (_session is { IsUsable: true } current)
            {
                return current;
            }

            if (_session is not null)
            {
                await _session.DisposeAsync().ConfigureAwait(false);
                _session = null;
            }

            IAppServerConnection connection;
            try
            {
                connection = _processFactory.Start(_options);
            }
            catch (CodexAppServerException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new CodexAppServerException("Unable to start the bundled Codex app server.", exception);
            }

            var session = new AppServerSession(connection);
            _session = session;
            session.ReaderTask = ReadLoopAsync(session);
            session.ErrorDrainTask = DrainStandardErrorAsync(session);

            try
            {
                var initializeParams = new
                {
                    clientInfo = new
                    {
                        name = _options.ClientName,
                        title = _options.ClientTitle,
                        version = _options.ClientVersion,
                    },
                    capabilities = (object?)null,
                };

                await SendRequestAsync(
                        session,
                        InitializeMethod,
                        initializeParams,
                        cancellationToken)
                    .ConfigureAwait(false);
                await SendNotificationAsync(
                        session,
                        InitializedMethod,
                        new { },
                        cancellationToken)
                    .ConfigureAwait(false);
                return session;
            }
            catch
            {
                _session = null;
                await session.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private async Task<JsonElement> SendRequestAsync(
        AppServerSession session,
        string method,
        object? parameters,
        CancellationToken cancellationToken)
    {
        EnsureAllowedRequestMethod(method);
        if (!session.IsUsable)
        {
            throw new CodexAppServerException("The Codex app server connection is not available.");
        }

        var id = Interlocked.Increment(ref _nextRequestId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!session.PendingRequests.TryAdd(id, completion))
        {
            throw new CodexAppServerProtocolException("Unable to allocate an app-server request identifier.");
        }

        try
        {
            var request = new OutboundRequest(id, method, parameters);
            await WriteLineAsync(session, JsonSerializer.Serialize(request), cancellationToken).ConfigureAwait(false);
            return await completion.Task
                .WaitAsync(_options.RequestTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException($"The Codex app server did not answer '{method}' in time.", exception);
        }
        finally
        {
            session.PendingRequests.TryRemove(id, out _);
        }
    }

    private async Task SendNotificationAsync(
        AppServerSession session,
        string method,
        object parameters,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(method, InitializedMethod, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Only the initialized notification may be sent.");
        }

        var notification = new OutboundNotification(method, parameters);
        await WriteLineAsync(session, JsonSerializer.Serialize(notification), cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteLineAsync(
        AppServerSession session,
        string json,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!session.IsUsable)
            {
                throw new CodexAppServerException("The Codex app server connection is not available.");
            }

            await session.Connection.StandardInput
                .WriteLineAsync(json.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            await session.Connection.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var wrapped = exception as CodexAppServerException
                ?? new CodexAppServerException("Unable to write to the Codex app server.", exception);
            session.Fail(wrapped);
            throw wrapped;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadLoopAsync(AppServerSession session)
    {
        try
        {
            while (!session.Lifetime.IsCancellationRequested)
            {
                var line = await session.Connection.StandardOutput
                    .ReadLineAsync(session.Lifetime.Token)
                    .ConfigureAwait(false);
                if (line is null)
                {
                    throw new EndOfStreamException("The Codex app server closed its output stream.");
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                DispatchInboundMessage(session, line);
            }
        }
        catch (OperationCanceledException) when (session.Lifetime.IsCancellationRequested)
        {
        }
        catch (JsonException exception)
        {
            session.Fail(new CodexAppServerProtocolException(
                "The Codex app server returned malformed JSONL data.",
                exception));
        }
        catch (Exception exception)
        {
            session.Fail(exception as CodexAppServerException
                ?? new CodexAppServerException("The Codex app server connection ended unexpectedly.", exception));
        }
    }

    private static async Task DrainStandardErrorAsync(AppServerSession session)
    {
        try
        {
            var buffer = new char[1024];
            while (await session.Connection.StandardError
                       .ReadAsync(buffer.AsMemory(), session.Lifetime.Token)
                       .ConfigureAwait(false) > 0)
            {
                // Deliberately discard stderr. It may contain paths or account-related details.
            }
        }
        catch (OperationCanceledException) when (session.Lifetime.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void DispatchInboundMessage(AppServerSession session, string line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        if (root.ValueKind is not JsonValueKind.Object)
        {
            throw new CodexAppServerProtocolException("An app-server JSONL message must be an object.");
        }

        var hasMethod = root.TryGetProperty("method", out var methodElement)
            && methodElement.ValueKind is JsonValueKind.String;
        var hasId = root.TryGetProperty("id", out var idElement);

        if (hasMethod && hasId)
        {
            throw new CodexAppServerProtocolException("Unexpected server request received from the Codex app server.");
        }

        if (hasId)
        {
            if (idElement.ValueKind is not JsonValueKind.Number || !idElement.TryGetInt64(out var id))
            {
                throw new CodexAppServerProtocolException("An app-server response used an invalid request identifier.");
            }

            if (!session.PendingRequests.TryRemove(id, out var completion))
            {
                return;
            }

            if (root.TryGetProperty("error", out var error) && error.ValueKind is JsonValueKind.Object)
            {
                var message = error.TryGetProperty("message", out var messageElement)
                              && messageElement.ValueKind is JsonValueKind.String
                    ? messageElement.GetString()
                    : "The Codex app server rejected the request.";
                int? code = error.TryGetProperty("code", out var codeElement)
                            && codeElement.TryGetInt32(out var parsedCode)
                    ? parsedCode
                    : null;
                completion.TrySetException(new CodexAppServerRequestException(
                    message ?? "The Codex app server rejected the request.",
                    code));
                return;
            }

            if (!root.TryGetProperty("result", out var result))
            {
                completion.TrySetException(new CodexAppServerProtocolException(
                    "An app-server response did not contain a result."));
                return;
            }

            completion.TrySetResult(result.Clone());
            return;
        }

        if (hasMethod)
        {
            if (string.Equals(
                    methodElement.GetString(),
                    RateLimitsUpdatedMethod,
                    StringComparison.Ordinal))
            {
                RaiseQuotaChanged();
            }

            return;
        }

        throw new CodexAppServerProtocolException("Unrecognized app-server JSONL message.");
    }

    private void RaiseQuotaChanged()
    {
        var handlers = QuotaChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch
            {
                // A UI subscriber must not be able to terminate the protocol reader.
            }
        }
    }

    private async Task InvalidateSessionAsync(AppServerSession failedSession)
    {
        await _connectionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (ReferenceEquals(_session, failedSession))
            {
                _session = null;
                await failedSession.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private static void EnsureAllowedRequestMethod(string method)
    {
        if (!string.Equals(method, InitializeMethod, StringComparison.Ordinal)
            && !string.Equals(method, ReadRateLimitsMethod, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The quota client attempted to send a non-read-only method.");
        }
    }

    private sealed class AppServerSession(IAppServerConnection connection) : IAsyncDisposable
    {
        private int _failed;
        private int _disposed;

        public IAppServerConnection Connection { get; } = connection;

        public CancellationTokenSource Lifetime { get; } = new();

        public ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> PendingRequests { get; } = new();

        public Task? ReaderTask { get; set; }

        public Task? ErrorDrainTask { get; set; }

        public bool IsUsable => Volatile.Read(ref _failed) == 0
            && Volatile.Read(ref _disposed) == 0
            && !Connection.HasExited;

        public void Fail(Exception exception)
        {
            if (Interlocked.Exchange(ref _failed, 1) != 0)
            {
                return;
            }

            foreach (var pending in PendingRequests.ToArray())
            {
                if (PendingRequests.TryRemove(pending.Key, out var completion))
                {
                    completion.TrySetException(exception);
                }
            }

            Lifetime.Cancel();
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            Fail(new ObjectDisposedException(nameof(CodexAppServerQuotaProvider)));
            Lifetime.Cancel();
            await Connection.DisposeAsync().ConfigureAwait(false);

            var currentTaskId = Task.CurrentId;
            if (ReaderTask is not null && ReaderTask.Id != currentTaskId)
            {
                await IgnoreFailureAsync(ReaderTask).ConfigureAwait(false);
            }

            if (ErrorDrainTask is not null && ErrorDrainTask.Id != currentTaskId)
            {
                await IgnoreFailureAsync(ErrorDrainTask).ConfigureAwait(false);
            }

            Lifetime.Dispose();
        }

        private static async Task IgnoreFailureAsync(Task task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private sealed record OutboundRequest(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("method")] string Method,
        [property: JsonPropertyName("params")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] object? Parameters);

    private sealed record OutboundNotification(
        [property: JsonPropertyName("method")] string Method,
        [property: JsonPropertyName("params")] object Parameters);
}
