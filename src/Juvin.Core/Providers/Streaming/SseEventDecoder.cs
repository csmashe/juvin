using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;

namespace Juvin.Core.Providers.Streaming;

/// <summary>A single dispatched server-sent event.</summary>
internal readonly record struct SseEvent(string Data, string EventType, string LastEventId);

/// <summary>WHATWG-compatible SSE framing.</summary>
internal static class SseEventDecoder
{
    public static async IAsyncEnumerable<SseEvent> DecodeAsync(
        TextReader reader,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var data = new StringBuilder();
        var eventType = "";
        var lastEventId = "";

        while (true)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException or SocketException)
            {
                throw MapBodyReadFailure(ex, cancellationToken);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw MapBodyReadFailure(ex, cancellationToken);
            }

            if (line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                if (data.Length == 0)
                {
                    // Per WHATWG dispatch, a blank line always resets the pending event
                    // type - an event-only block must not label the next event.
                    eventType = "";
                    continue;
                }

                data.Length--;
                yield return new SseEvent(
                    data.ToString(),
                    eventType.Length == 0 ? "message" : eventType,
                    lastEventId
                );
                data.Clear();
                eventType = "";
                continue;
            }

            if (line[0] == ':')
            {
                continue;
            }

            var colonIndex = line.IndexOf(':');
            var field = colonIndex < 0 ? line : line[..colonIndex];
            var value = colonIndex < 0 ? "" : line[(colonIndex + 1)..];
            if (value.StartsWith(' '))
            {
                value = value[1..];
            }

            switch (field)
            {
                case "data":
                    data.Append(value);
                    data.Append('\n');
                    break;
                case "event":
                    eventType = value;
                    break;
                case "id" when !value.Contains('\0'):
                    lastEventId = value;
                    break;
            }
        }
    }

    /// <summary>Classifies a transport failure while reading a response body.</summary>
    /// <exception cref="OperationCanceledException">The caller cancelled the operation.</exception>
    public static ProviderException MapBodyReadFailure(Exception ex, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return new ProviderException(
            ProviderFailureKind.Unreachable,
            ex is OperationCanceledException
                ? "The model server stopped responding."
                : "The connection to the model server was lost.",
            ex
        );
    }
}
