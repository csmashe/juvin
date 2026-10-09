using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Juvin.Platform;

/// <summary>
///     Framing shared by every local socket implementation: one UTF-8 line of JSON per request
///     and per reply, one exchange per connection.
/// </summary>
public static class LocalSocketProtocol
{
    /// <summary>
    ///     Hard cap on a single line. The server stops reading at the cap so a hostile or buggy
    ///     client cannot exhaust process memory.
    /// </summary>
    public const int MaxLineBytes = 4 * 1024;

    public const int CurrentVersion = 1;

    public const string ErrLineTooLong = "line-too-long";
    public const string ErrReplyTooLong = "reply-too-long";
    public const string ErrInternal = "internal";
    public const string ErrBadRequest = "bad-request";
    public const string ErrUnsupportedVersion = "unsupported-version";
    public const string ErrUnknownCommand = "unknown-command";

    private static readonly UTF8Encoding s_utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static string SerializeOk()
    {
        return JsonSerializer.Serialize(new OkReply(CurrentVersion, true));
    }

    public static string SerializeError(string code)
    {
        return JsonSerializer.Serialize(new ErrorReply(CurrentVersion, false, code));
    }

    /// <summary>
    ///     Reads up to <paramref name="maxBytes" /> or until a newline. Returns the line without
    ///     its newline, or null when the peer closed before sending anything. <c>TooLong</c> is
    ///     true when the cap was reached without a newline.
    /// </summary>
    public static async Task<(string? Line, bool TooLong)> ReadLineAsync(
        Stream stream,
        int maxBytes,
        CancellationToken ct
    )
    {
        var buffer = new byte[maxBytes];
        var total = 0;
        while (total < maxBytes)
        {
            var read = await stream
                .ReadAsync(buffer.AsMemory(total, maxBytes - total), ct)
                .ConfigureAwait(false);
            if (read <= 0)
            {
                return (total == 0 ? null : s_utf8NoBom.GetString(buffer, 0, total), false);
            }

            var newline = Array.IndexOf(buffer, (byte)'\n', total, read);
            total += read;
            if (newline >= 0)
            {
                return (s_utf8NoBom.GetString(buffer, 0, newline).TrimEnd('\r'), false);
            }
        }

        return (null, true);
    }

    /// <summary>
    ///     Server side of one exchange: reads the request line and returns the reply to send, or
    ///     null when the peer closed without sending anything. Oversized requests, handler
    ///     failures, and oversized or multi-line replies become error replies. A handler that
    ///     ignores <paramref name="ct" /> is abandoned when it fires.
    /// </summary>
    public static async Task<string?> ReadAndHandleAsync(
        Stream stream,
        Func<string, CancellationToken, Task<string>> handleLine,
        CancellationToken ct
    )
    {
        var (line, tooLong) = await ReadLineAsync(stream, MaxLineBytes, ct).ConfigureAwait(false);
        if (tooLong)
        {
            return SerializeError(ErrLineTooLong);
        }

        if (line is null)
        {
            return null;
        }

        try
        {
            var reply = await handleLine(line, ct).WaitAsync(ct).ConfigureAwait(false);
            if (reply.Contains('\n') || reply.Contains('\r'))
            {
                Trace.WriteLine("[LocalSocketProtocol] Handler returned a multi-line reply.");
                return SerializeError(ErrInternal);
            }

            // ReSharper disable once InvertIf -- rejections stay guard clauses so the success path ends the block
            if (s_utf8NoBom.GetByteCount(reply) + 1 > MaxLineBytes)
            {
                Trace.WriteLine("[LocalSocketProtocol] Handler returned an oversized reply.");
                return SerializeError(ErrReplyTooLong);
            }

            return reply;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The exception message may quote request content, so only the type is logged.
            Trace.WriteLine($"[LocalSocketProtocol] Handler threw {ex.GetType().Name}.");
            return SerializeError(ErrInternal);
        }
    }

    /// <summary>
    ///     Writes one line without flushing: a named pipe flush blocks until the peer has read,
    ///     which deadlocks against a peer still writing, and sockets deliver without it.
    /// </summary>
    public static async Task WriteLineAsync(Stream stream, string line, CancellationToken ct)
    {
        await stream.WriteAsync(s_utf8NoBom.GetBytes(line + "\n"), ct).ConfigureAwait(false);
    }

    /// <summary>Rejects lines the server would refuse, before anything is sent.</summary>
    public static void ThrowIfInvalidRequest(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        if (line.Contains('\n') || line.Contains('\r'))
        {
            throw new ArgumentException("A request must be a single line.", nameof(line));
        }

        if (s_utf8NoBom.GetByteCount(line) + 1 > MaxLineBytes)
        {
            throw new ArgumentException(
                $"A request may not exceed {MaxLineBytes} bytes.",
                nameof(line)
            );
        }
    }

    // ReSharper disable NotAccessedPositionalProperty.Local -- read by System.Text.Json reflection when serialized
    private sealed record ErrorReply(
        [property: JsonPropertyName("v")] int Version,
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("error")] string Error
    );

    private sealed record OkReply(
        [property: JsonPropertyName("v")] int Version,
        [property: JsonPropertyName("ok")] bool Ok
    );
    // ReSharper restore NotAccessedPositionalProperty.Local
}
