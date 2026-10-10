using System.Collections.Concurrent;
using System.Diagnostics;

namespace Juvin.Core.Connector;

/// <summary>A request that could not complete: the connection closed, or the peer answered with an error.</summary>
public sealed class ConnectorException(string code, string message) : IOException(message)
{
    public string Code { get; } = code;
}

/// <summary>The outcome of a <c>context.request</c>: a capture, or the extension's error.</summary>
public sealed record ContextResult(CapturedContext? Capture, ConnectorError? Error)
{
    public bool Ok => Capture is not null;
}

/// <summary>
///     The app side of one extension connection: answers <c>hello</c> and <c>ping</c>, raises
///     <see cref="ShortcutPressed" />, and sends requests whose responses it matches by id.
/// </summary>
/// <remarks>
///     Events are raised on the receive loop's thread, which is a thread-pool thread. Handlers
///     must return quickly and marshal to the UI thread themselves. Never logs message content.
/// </remarks>
public sealed class ConnectorSession : IAsyncDisposable
{
    private readonly IConnectorChannel _channel;
    private readonly Func<ConnectorConfig> _config;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ConnectorMessage>> _pending = new();

    private volatile HelloMessage? _client;
    private int _closed;
    private long _nextId;

    /// <param name="channel">The framed channel to the extension; disposed with the session.</param>
    /// <param name="config">Builds the config sent in reply to <c>hello</c>.</param>
    public ConnectorSession(IConnectorChannel channel, Func<ConnectorConfig> config)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(config);

        _channel = channel;
        _config = config;
    }

    /// <summary>The extension's identity from its last <c>hello</c>; null until it has sent one.</summary>
    public HelloMessage? Client => _client;

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>Raised after a <c>hello</c> has been answered with config.</summary>
    public event Action<ConnectorSession>? HelloReceived;

    public event Action<ConnectorSession>? ShortcutPressed;

    /// <summary>Runs the receive loop until the peer disconnects, the protocol requires closing, or <paramref name="ct" /> fires.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        var token = linked.Token;
        try
        {
            while (await _channel.ReceiveAsync(token).ConfigureAwait(false) is { } json)
            {
                if (!await HandleAsync(json, token).ConfigureAwait(false))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
        catch (ConnectorFrameException ex)
        {
            Trace.WriteLine($"[ConnectorSession] Closing after a bad frame ({ex.Failure}).");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Trace.WriteLine($"[ConnectorSession] Connection lost ({ex.GetType().Name}).");
        }
        finally
        {
            await CloseAsync().ConfigureAwait(false);
        }
    }

    public Task SendConfigAsync(ConnectorConfig config, CancellationToken ct) => SendAsync(config, ct);

    /// <summary>Asks the extension to capture the active tab.</summary>
    /// <exception cref="TimeoutException">No response within <paramref name="timeout" />.</exception>
    /// <exception cref="ConnectorException">The connection closed first.</exception>
    public async Task<ContextResult> RequestContextAsync(TimeSpan timeout, CancellationToken ct)
    {
        var response = await RequestAsync(new ContextRequestMessage { Id = NextId("c") }, timeout, ct)
            .ConfigureAwait(false);
        return response switch
        {
            ContextResponseMessage { Ok: true, Capture: { } capture } => new ContextResult(CapturedContext.From(capture), null),
            ContextResponseMessage { Error: { } error } => new ContextResult(null, error),
            _ => new ContextResult(null, ErrorOf(response)),
        };
    }

    /// <summary>Asks the extension to replace the captured selection. Returns null on success.</summary>
    /// <exception cref="TimeoutException">No response within <paramref name="timeout" />.</exception>
    /// <exception cref="ConnectorException">The connection closed first.</exception>
    public async Task<ConnectorError?> ApplyAsync(
        string editorId,
        string expectedHash,
        int start,
        int end,
        string text,
        TimeSpan timeout,
        CancellationToken ct
    )
    {
        var request = new ApplyRequestMessage
        {
            Id = NextId("a"),
            EditorId = editorId,
            ExpectedHash = expectedHash,
            Start = start,
            End = end,
            Text = text,
        };
        var response = await RequestAsync(request, timeout, ct).ConfigureAwait(false);
        return response switch
        {
            ApplyResponseMessage { Ok: true } => null,
            ApplyResponseMessage { Error: { } error } => error,
            _ => ErrorOf(response),
        };
    }

    /// <summary>Returns the round-trip time of a <c>ping</c>.</summary>
    /// <exception cref="TimeoutException">No <c>pong</c> within <paramref name="timeout" />.</exception>
    /// <exception cref="ConnectorException">The connection closed first or the extension answered with an error.</exception>
    public async Task<TimeSpan> PingAsync(TimeSpan timeout, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var response = await RequestAsync(new PingMessage { Id = NextId("p") }, timeout, ct).ConfigureAwait(false);
        // ReSharper disable once InvertIf -- a guard clause reads better than the success case nested in an if
        if (response is not PongMessage)
        {
            var error = ErrorOf(response);
            throw new ConnectorException(error.Code, error.Message);
        }

        return Stopwatch.GetElapsedTime(started);
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await CloseAsync().ConfigureAwait(false);
    }

    private static ConnectorError ErrorOf(ConnectorMessage response) =>
        response is ErrorMessage { Error: { } error }
            ? error
            : new ConnectorError(ConnectorErrorCodes.BadMessage, $"Unexpected {response.Type} reply.");

    private string NextId(string prefix) => prefix + Interlocked.Increment(ref _nextId);

    /// <summary>Returns false when the connection must close.</summary>
    private async Task<bool> HandleAsync(string json, CancellationToken ct)
    {
        var parsed = ConnectorProtocol.Parse(json);
        var (message, rejection) = parsed;
        if (rejection is not null)
        {
            Trace.WriteLine($"[ConnectorSession] Rejected a message ({rejection.Error.Code}, {json.Length} chars).");
            if (!parsed.RejectedErrorReply)
            {
                await TrySendAsync(rejection, ct).ConfigureAwait(false);
            }

            return rejection.Error.Code != ConnectorErrorCodes.UnsupportedVersion;
        }

        if (message is HelloMessage hello)
        {
            _client = hello;
            await TrySendAsync(_config(), ct).ConfigureAwait(false);
            Raise(HelloReceived);
            return true;
        }

        if (_client is null)
        {
            if (message is ErrorMessage early)
            {
                Trace.WriteLine($"[ConnectorSession] Ignored an error before hello ({early.Error.Code}).");
                return true;
            }

            await TrySendAsync(
                    ConnectorProtocol.CreateError(message!.Id, ConnectorErrorCodes.HelloRequired, "Send hello first."),
                    ct
                )
                .ConfigureAwait(false);
            return true;
        }

        switch (message)
        {
            case PingMessage ping:
                await TrySendAsync(new PongMessage { Id = ping.Id }, ct).ConfigureAwait(false);
                break;
            case ShortcutPressedMessage:
                Raise(ShortcutPressed);
                break;
            case PongMessage or ContextResponseMessage or ApplyResponseMessage or ErrorMessage:
                Complete(message);
                break;
            default:
                // config, context.request, and apply.request only travel from the app to the extension.
                await TrySendAsync(
                        ConnectorProtocol.CreateError(
                            message!.Id,
                            ConnectorErrorCodes.UnknownType,
                            $"{message.Type} is not accepted by the app."
                        ),
                        ct
                    )
                    .ConfigureAwait(false);
                break;
        }

        return true;
    }

    private void Complete(ConnectorMessage response)
    {
        if (response.Id is not null && _pending.TryGetValue(response.Id, out var waiter))
        {
            waiter.TrySetResult(response);
            return;
        }

        var code = response is ErrorMessage error ? error.Error.Code : "-";
        Trace.WriteLine($"[ConnectorSession] Ignored an unmatched {response.Type} (error {code}).");
    }

    private async Task<ConnectorMessage> RequestAsync(ConnectorMessage request, TimeSpan timeout, CancellationToken ct)
    {
        if (_client is null)
        {
            throw new InvalidOperationException("The extension has not sent hello yet.");
        }

        var id = request.Id!;
        var waiter = new TaskCompletionSource<ConnectorMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = waiter;
        try
        {
            // Checked after registering so a close that races with this call still fails the waiter.
            if (IsClosed)
            {
                throw Disconnected();
            }

            // One deadline for the send and the wait: a peer that stops reading must not stall the request.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(timeout);
            try
            {
                await SendAsync(request, deadline.Token).ConfigureAwait(false);
                return await waiter.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"No response to {request.Type} within {timeout}.");
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task SendAsync(ConnectorMessage message, CancellationToken ct)
    {
        if (IsClosed)
        {
            throw Disconnected();
        }

        try
        {
            await _channel.SendAsync(ConnectorProtocol.Serialize(message), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is (IOException or ObjectDisposedException) and not ConnectorFrameException)
        {
            throw Disconnected();
        }
    }

    private async Task TrySendAsync(ConnectorMessage message, CancellationToken ct)
    {
        try
        {
            await SendAsync(message, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException)
        {
            Trace.WriteLine($"[ConnectorSession] Could not send {message.Type} ({ex.GetType().Name}).");
        }
    }

    private void Raise(Action<ConnectorSession>? handler)
    {
        try
        {
            handler?.Invoke(this);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ConnectorSession] An event handler threw {ex.GetType().Name}.");
        }
    }

    private async Task CloseAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1)
        {
            return;
        }

        foreach (var waiter in _pending.Values)
        {
            waiter.TrySetException(Disconnected());
        }

        await _channel.DisposeAsync().ConfigureAwait(false);
    }

    private static ConnectorException Disconnected() =>
        new(ConnectorErrorCodes.Disconnected, "The connector connection closed.");
}
