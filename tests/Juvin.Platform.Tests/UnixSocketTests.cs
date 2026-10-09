using System.Net.Sockets;
using System.Runtime.Versioning;
using Juvin.Platform.Unix;
using Juvin.Platform.Unix.Ipc;

namespace Juvin.Platform.Tests;

[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public sealed class UnixSocketTests : IDisposable
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(5);

    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        _temp.Dispose();
    }

    private string SocketPath => _temp.File("control.sock");

    [UnixFact]
    public async Task SendAsync_ReceivesTheHandlersReply()
    {
        using var server = new UnixSocketServer(SocketPath);
        await server.StartAsync((line, _) => Task.FromResult("echo:" + line), CancellationToken.None);

        var reply = await new UnixSocketClient(SocketPath).SendAsync("""{"v":1}""", s_timeout);

        Assert.Equal("""echo:{"v":1}""", reply);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(SocketPath));
    }

    [UnixFact]
    public async Task Server_RepliesLineTooLongWhenTheCapIsExceeded()
    {
        using var server = new UnixSocketServer(SocketPath);
        var handled = false;
        await server.StartAsync(
            (_, _) =>
            {
                handled = true;
                return Task.FromResult("ok");
            },
            CancellationToken.None
        );

        using var socket = NewUnixSocket();
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(SocketPath));
        await using var stream = new NetworkStream(socket);
        await stream.WriteAsync(Enumerable.Repeat((byte)'a', LocalSocketProtocol.MaxLineBytes + 10).ToArray());
        var (reply, _) = await LocalSocketProtocol.ReadLineAsync(stream, LocalSocketProtocol.MaxLineBytes, CancellationToken.None);

        Assert.Contains(LocalSocketProtocol.ErrLineTooLong, reply);
        Assert.False(handled);
    }

    [UnixFact]
    public async Task Server_RepliesInternalErrorWhenTheHandlerThrows()
    {
        using var server = new UnixSocketServer(SocketPath);
        await server.StartAsync((_, _) => throw new InvalidOperationException("boom"), CancellationToken.None);

        var reply = await new UnixSocketClient(SocketPath).SendAsync("{}", s_timeout);

        Assert.Equal(LocalSocketProtocol.SerializeError(LocalSocketProtocol.ErrInternal), reply);
    }

    [UnixFact]
    public async Task Server_RepliesReplyTooLongWhenTheHandlerExceedsTheCap()
    {
        using var server = new UnixSocketServer(SocketPath);
        await server.StartAsync(
            (_, _) => Task.FromResult(new string('a', LocalSocketProtocol.MaxLineBytes)),
            CancellationToken.None
        );

        var reply = await new UnixSocketClient(SocketPath).SendAsync("{}", s_timeout);

        Assert.Equal(LocalSocketProtocol.SerializeError(LocalSocketProtocol.ErrReplyTooLong), reply);
    }

    [UnixFact]
    public async Task StopAsync_CancelsAStalledHandler()
    {
        using var server = new UnixSocketServer(SocketPath);
        var handlerToken = new TaskCompletionSource<CancellationToken>();
        await server.StartAsync(
            async (_, ct) =>
            {
                handlerToken.SetResult(ct);
                await Task.Delay(Timeout.Infinite, ct);
                return "never";
            },
            CancellationToken.None
        );

        var send = new UnixSocketClient(SocketPath).SendAsync("{}", s_timeout);
        var ct = await handlerToken.Task.WaitAsync(s_timeout, CancellationToken.None);
        await server.StopAsync();

        Assert.True(ct.IsCancellationRequested);
        Assert.Null(await send);
    }

    [UnixFact]
    public async Task SendAsync_RejectsOversizedAndMultiLineRequests()
    {
        var client = new UnixSocketClient(SocketPath);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.SendAsync(new string('a', LocalSocketProtocol.MaxLineBytes), s_timeout)
        );
        await Assert.ThrowsAsync<ArgumentException>(() => client.SendAsync("a\nb", s_timeout));
    }

    [UnixFact]
    public async Task Client_ReportsNoPeerWhenNothingIsListening()
    {
        var client = new UnixSocketClient(SocketPath);

        Assert.False(client.IsLivePeer());
        Assert.Null(await client.SendAsync("{}", s_timeout));
    }

    [UnixFact]
    public async Task Client_SeesLivePeerUntilTheServerStops()
    {
        var server = new UnixSocketServer(SocketPath);
        await server.StartAsync((_, _) => Task.FromResult("ok"), CancellationToken.None);
        var client = new UnixSocketClient(SocketPath);

        Assert.True(client.IsLivePeer());

        await server.StopAsync();

        Assert.False(client.IsLivePeer());
        Assert.False(File.Exists(SocketPath));
    }

    [UnixFact]
    public async Task SecondServer_FailsWhileTheFirstIsRunning()
    {
        using var first = new UnixSocketServer(SocketPath);
        await first.StartAsync((_, _) => Task.FromResult("ok"), CancellationToken.None);
        using var second = new UnixSocketServer(SocketPath);

        await Assert.ThrowsAsync<IOException>(() => second.StartAsync((_, _) => Task.FromResult("ok"), CancellationToken.None));
    }

    [UnixFact]
    public async Task Server_RemovesAStaleSocketFileOnStart()
    {
        // .NET unlinks a socket file when the socket that bound it is closed, so bind elsewhere
        // and move the file into place to leave a socket nobody listens on.
        var boundPath = _temp.File("stale.sock");
        using (var stale = NewUnixSocket())
        {
            stale.Bind(new UnixDomainSocketEndPoint(boundPath));
            File.Move(boundPath, SocketPath);
        }

        Assert.True(File.Exists(SocketPath));

        using var server = new UnixSocketServer(SocketPath);
        await server.StartAsync((_, _) => Task.FromResult("fresh"), CancellationToken.None);

        Assert.Equal("fresh", await new UnixSocketClient(SocketPath).SendAsync("{}", s_timeout));
    }

    [UnixFact]
    public async Task Guard_AllowsOneHolderAndLendsOwnershipToItsServer()
    {
        using var guard = new UnixSingleInstanceGuard(SocketPath);
        using var other = new UnixSingleInstanceGuard(SocketPath);

        Assert.True(guard.TryAcquire());
        Assert.True(guard.TryAcquire());
        Assert.False(other.TryAcquire());

        using var server = new UnixSocketServer(SocketPath, guard);
        await server.StartAsync((_, _) => Task.FromResult("ok"), CancellationToken.None);
        using var unguarded = new UnixSocketServer(SocketPath);

        await Assert.ThrowsAsync<IOException>(() => unguarded.StartAsync((_, _) => Task.FromResult("ok"), CancellationToken.None));
        Assert.Equal("ok", await new UnixSocketClient(SocketPath).SendAsync("{}", s_timeout));
    }

    [UnixFact]
    public void Guard_CanBeReacquiredAfterRelease()
    {
        var first = new UnixSingleInstanceGuard(SocketPath);
        Assert.True(first.TryAcquire());
        first.Dispose();

        using var second = new UnixSingleInstanceGuard(SocketPath);
        Assert.True(second.TryAcquire());
    }

    private static Socket NewUnixSocket() => new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
}
