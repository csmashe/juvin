using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Juvin.Core.Tests.Fakes;

/// <summary>What the server answers every request with.</summary>
internal sealed record LoopbackResponse(HttpStatusCode Status, string? Location = null);

/// <summary>
///     A real HTTP server on a free loopback port that answers every request the same way and
///     counts them. Built on a plain socket so it needs no URL reservation on Windows.
/// </summary>
internal sealed class LoopbackHttpServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Task _loop;
    private int _requests;

    public LoopbackHttpServer(Func<LoopbackResponse> respond)
    {
        _listener.Start();
        Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";
        _loop = Task.Run(() => ServeAsync(respond));
    }

    public string Url { get; }

    public int Requests => Volatile.Read(ref _requests);

    public void Dispose()
    {
        _listener.Dispose();
        _loop.Wait(TimeSpan.FromSeconds(5));
    }

    private async Task ServeAsync(Func<LoopbackResponse> respond)
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync();
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                return;
            }

            using (client)
            {
                await using var stream = client.GetStream();
                await ReadRequestAsync(stream);
                Interlocked.Increment(ref _requests);
                var response = respond();
                var head = new StringBuilder()
                    .Append("HTTP/1.1 ").Append((int)response.Status).Append(' ').Append(response.Status).Append("\r\n")
                    .Append("Content-Length: 0\r\nConnection: close\r\n");
                if (response.Location is not null)
                {
                    head.Append("Location: ").Append(response.Location).Append("\r\n");
                }

                await stream.WriteAsync(Encoding.ASCII.GetBytes(head.Append("\r\n").ToString()));
            }
        }
    }

    /// <summary>Reads the head and, when a Content-Length is given, the body, so the client is not cut off mid-send.</summary>
    private static async Task ReadRequestAsync(NetworkStream stream)
    {
        var buffer = new byte[8192];
        using var head = new MemoryStream();
        var headEnd = -1;
        while (headEnd < 0)
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0)
            {
                return;
            }

            head.Write(buffer, 0, read);
            headEnd = Encoding.ASCII.GetString(head.ToArray()).IndexOf("\r\n\r\n", StringComparison.Ordinal);
        }

        var text = Encoding.ASCII.GetString(head.ToArray());
        var contentLength = text[..headEnd]
            .Split("\r\n")
            .Select(line => line.Split(':', 2))
            .Where(parts => parts.Length == 2 && parts[0].Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            .Select(parts => int.Parse(parts[1].Trim()))
            .FirstOrDefault();
        var remaining = contentLength - (head.Length - headEnd - 4);
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)));
            if (read == 0)
            {
                return;
            }

            remaining -= read;
        }
    }
}
