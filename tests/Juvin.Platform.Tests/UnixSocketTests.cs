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
    public async Task Client_ReturnsNullWhenNothingIsListening()
    {
        Assert.Null(await new UnixSocketClient(SocketPath).SendAsync("{}", s_timeout));
    }

    [UnixFact]
    public async Task Client_GetsRepliesUntilTheServerStops()
    {
        var server = new UnixSocketServer(SocketPath);
        await server.StartAsync((_, _) => Task.FromResult("ok"), CancellationToken.None);
        var client = new UnixSocketClient(SocketPath);

        Assert.Equal("ok", await client.SendAsync("{}", s_timeout));

        await server.StopAsync();

        Assert.Null(await client.SendAsync("{}", s_timeout));
        Assert.False(File.Exists(SocketPath));
    }

    [UnixFact]
    public async Task SendWhenReady_ReachesAServerThatStartsLate()
    {
        using var guard = new UnixSingleInstanceGuard(SocketPath);
        Assert.True(guard.TryAcquire());
        var send = new UnixSocketClient(SocketPath).SendWhenReadyAsync("show", TimeSpan.FromSeconds(3));

        await Task.Delay(500);
        using var server = new UnixSocketServer(SocketPath, guard);
        await server.StartAsync((line, _) => Task.FromResult("echo:" + line), CancellationToken.None);

        Assert.Equal("echo:show", await send);
    }

    [UnixFact]
    public async Task Client_AcceptsAServerRunByTheSameUser()
    {
        using var server = new UnixSocketServer(SocketPath);
        await server.StartAsync((_, _) => Task.FromResult("ok"), CancellationToken.None);

        using var socket = NewUnixSocket();
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(SocketPath));

        Assert.True(UnixPeerCredentials.IsOwnedByEffectiveUser(socket));
    }

    [UnixFact]
    public async Task Client_LeavesARefusingSocketInPlaceWhileTheOwnerHoldsTheLock()
    {
        using var guard = new UnixSingleInstanceGuard(SocketPath);
        Assert.True(guard.TryAcquire());
        LeaveStaleSocket();

        Assert.Null(await new UnixSocketClient(SocketPath).SendAsync("{}", s_timeout));
        Assert.True(File.Exists(SocketPath));
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
        LeaveStaleSocket();

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
        using var first = new UnixSingleInstanceGuard(SocketPath);
        Assert.True(first.TryAcquire());
        // ReSharper disable once DisposeOnUsingVariable -- the mid-test Dispose is the behaviour under test; the using only covers a failing assertion
        first.Dispose();

        using var second = new UnixSingleInstanceGuard(SocketPath);
        Assert.True(second.TryAcquire());
    }

    [UnixFact]
    public async Task SocketPathOverTheLimit_FailsOnlyWhereTheSocketIsUsed()
    {
        var path = SocketPathResolver.ResolveSocketPath("control.sock", _temp.File(new string('a', 110)), runtimeDirectory: null);
        using var guard = new UnixSingleInstanceGuard(path);
        using var server = new UnixSocketServer(path, guard);
        using var connector = new UnixConnectorSocketServer(path);

        Assert.True(guard.TryAcquire());
        await Assert.ThrowsAsync<IOException>(() => server.StartAsync((_, _) => Task.FromResult("ok"), CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(() => connector.StartAsync((_, _) => Task.CompletedTask, CancellationToken.None));
        Assert.Null(await new UnixSocketClient(path).SendAsync("{}", s_timeout));
        Assert.Null(await new UnixConnectorSocketClient(path).ConnectAsync(s_timeout));
    }

    [LinuxFact]
    public void Resolver_UsesAPrivateRuntimeDirectory()
    {
        var runtime = _temp.File("run");
        Directory.CreateDirectory(runtime, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var path = SocketPathResolver.ResolveSocketPath("control.sock", _temp.File("data"), runtime);

        Assert.Equal(Path.Join(runtime, "juvin", "control.sock"), path);
    }

    [LinuxFact]
    public void Resolver_FallsBackWhenTheRuntimeDirectoryIsWritableByOthers()
    {
        var runtime = _temp.File("run");
        Directory.CreateDirectory(runtime);
        File.SetUnixFileMode(runtime, (UnixFileMode)0b111_111_111);
        var fallback = _temp.File("data");

        var path = SocketPathResolver.ResolveSocketPath("control.sock", fallback, runtime);

        Assert.Equal(Path.Join(fallback, "control.sock"), path);
        Assert.False(Directory.Exists(Path.Join(runtime, "juvin")));
    }

    /// <summary>
    ///     .NET unlinks a socket file when the socket that bound it is closed, so this binds elsewhere
    ///     and moves the file into place to leave a socket nobody listens on.
    /// </summary>
    private void LeaveStaleSocket()
    {
        var boundPath = _temp.File("stale.sock");
        using var stale = NewUnixSocket();
        stale.Bind(new UnixDomainSocketEndPoint(boundPath));
        File.Move(boundPath, SocketPath);
    }

    private static Socket NewUnixSocket() => new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
}
