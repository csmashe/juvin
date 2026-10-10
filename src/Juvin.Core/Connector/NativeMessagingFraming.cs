using System.Buffers.Binary;
using System.Text;

namespace Juvin.Core.Connector;

public enum ConnectorFrameFailure
{
    TooLarge,
    Truncated,
}

/// <summary>A frame that breaks the framing rules. The connection cannot continue after one.</summary>
public sealed class ConnectorFrameException(ConnectorFrameFailure failure, string message) : IOException(message)
{
    public ConnectorFrameFailure Failure { get; } = failure;
}

/// <summary>
///     Native messaging framing, used on stdin/stdout and on the connector endpoint alike: a 4-byte
///     little-endian length followed by that many bytes of UTF-8 JSON.
/// </summary>
public static class NativeMessagingFraming
{
    /// <summary>The browsers' own limit for host-to-browser messages.</summary>
    public const int MaxFrameBytes = 1_048_576;

    private const int HeaderBytes = 4;

    private static readonly UTF8Encoding s_utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Returns the next frame's JSON, or null when the peer closed between frames.</summary>
    /// <exception cref="ConnectorFrameException">The frame is over the cap or the stream ended inside it.</exception>
    public static async Task<string?> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var header = new byte[HeaderBytes];
        var headerRead = await stream
            .ReadAtLeastAsync(header, HeaderBytes, throwOnEndOfStream: false, ct)
            .ConfigureAwait(false);
        // ReSharper disable once ConvertIfStatementToSwitchStatement -- two guards on a count, not a dispatch
        if (headerRead == 0)
        {
            return null;
        }

        if (headerRead < HeaderBytes)
        {
            throw new ConnectorFrameException(ConnectorFrameFailure.Truncated, "The stream ended inside a frame header.");
        }

        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length > MaxFrameBytes)
        {
            throw TooLarge(length);
        }

        var body = new byte[length];
        var bodyRead = await stream
            .ReadAtLeastAsync(body, body.Length, throwOnEndOfStream: false, ct)
            .ConfigureAwait(false);
        // ReSharper disable once ConvertIfStatementToReturnStatement -- a guard clause reads better than a throw inside a conditional
        if (bodyRead < body.Length)
        {
            throw new ConnectorFrameException(ConnectorFrameFailure.Truncated, "The stream ended inside a frame body.");
        }

        return s_utf8NoBom.GetString(body);
    }

    /// <summary>
    ///     Writes one frame in a single write and does not flush: a named pipe flush blocks until
    ///     the peer has read, and sockets deliver without it.
    /// </summary>
    /// <exception cref="ConnectorFrameException">The encoded JSON is over the cap.</exception>
    public static async Task WriteFrameAsync(Stream stream, string json, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(json);

        var length = s_utf8NoBom.GetByteCount(json);
        if (length > MaxFrameBytes)
        {
            throw TooLarge((uint)length);
        }

        var frame = new byte[HeaderBytes + length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)length);
        s_utf8NoBom.GetBytes(json, frame.AsSpan(HeaderBytes));
        await stream.WriteAsync(frame, ct).ConfigureAwait(false);
    }

    private static ConnectorFrameException TooLarge(uint length) =>
        new(ConnectorFrameFailure.TooLarge, $"A frame of {length} bytes exceeds the {MaxFrameBytes} byte cap.");
}
