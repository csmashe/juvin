using System.Runtime.Versioning;
using Juvin.Core.Connector;
using Juvin.Platform.Unix.Ipc;

namespace Juvin.Platform.Tests;

[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public sealed class UnixConnectorSocketTests : IDisposable
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(5);

    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        _temp.Dispose();
    }

    private string SocketPath => _temp.File("connector.sock");

    [UnixFact]
    public async Task Connection_CarriesFramesBothWaysIncludingLargeOnes()
    {
        using var server = new UnixConnectorSocketServer(SocketPath);
        await server.StartAsync(EchoFramesAsync, CancellationToken.None);
        var large = "\"" + new string('x', 300 * 1024) + "\"";

        await using var stream = await new UnixConnectorSocketClient(SocketPath).ConnectAsync(s_timeout);
        Assert.NotNull(stream);
        await NativeMessagingFraming.WriteFrameAsync(stream, """{"v":1}""", CancellationToken.None);
        await NativeMessagingFraming.WriteFrameAsync(stream, large, CancellationToken.None);

        // Reaching the handler at all means the peer-credential check accepted this user.
        Assert.Equal("""{"v":1}""", await NativeMessagingFraming.ReadFrameAsync(stream, CancellationToken.None).WaitAsync(s_timeout));
        Assert.Equal(large, await NativeMessagingFraming.ReadFrameAsync(stream, CancellationToken.None).WaitAsync(s_timeout));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(SocketPath));
    }

    [UnixFact]
    public async Task Connection_StaysOpenAcrossIdleTimeAndServerSends()
    {
        using var server = new UnixConnectorSocketServer(SocketPath);
        await server.StartAsync(
            async (stream, ct) =>
            {
                await Task.Delay(200, ct);
                await NativeMessagingFraming.WriteFrameAsync(stream, "\"unprompted\"", ct);
                await EchoFramesAsync(stream, ct);
            },
            CancellationToken.None
        );

        await using var stream = await new UnixConnectorSocketClient(SocketPath).ConnectAsync(s_timeout);

        Assert.Equal("\"unprompted\"", await NativeMessagingFraming.ReadFrameAsync(stream!, CancellationToken.None).WaitAsync(s_timeout));
        await NativeMessagingFraming.WriteFrameAsync(stream!, "1", CancellationToken.None);
        Assert.Equal("1", await NativeMessagingFraming.ReadFrameAsync(stream!, CancellationToken.None).WaitAsync(s_timeout));
    }

    [UnixFact]
    public async Task StopAsync_ClosesOpenConnectionsAndRemovesTheSocket()
    {
        var server = new UnixConnectorSocketServer(SocketPath);
        var handlerToken = new TaskCompletionSource<CancellationToken>();
        await server.StartAsync(
            async (stream, ct) =>
            {
                handlerToken.SetResult(ct);
                await EchoFramesAsync(stream, ct);
            },
            CancellationToken.None
        );

        await using var stream = await new UnixConnectorSocketClient(SocketPath).ConnectAsync(s_timeout);
        // ReSharper disable once MethodSupportsCancellation -- the token in scope is the handler's, the thing under test
        var ct = await handlerToken.Task.WaitAsync(s_timeout);
        await server.StopAsync();

        Assert.True(ct.IsCancellationRequested);
        // ReSharper disable once MethodSupportsCancellation -- the token in scope is the handler's, the thing under test
        Assert.Null(await NativeMessagingFraming.ReadFrameAsync(stream!, CancellationToken.None).WaitAsync(s_timeout));
        Assert.False(File.Exists(SocketPath));
    }

    [UnixFact]
    public async Task Client_ReturnsNullWhenNothingIsListening()
    {
        Assert.Null(await new UnixConnectorSocketClient(SocketPath).ConnectAsync(s_timeout));
    }

    [UnixFact]
    public async Task SecondServer_FailsWhileTheFirstIsRunning()
    {
        using var first = new UnixConnectorSocketServer(SocketPath);
        await first.StartAsync(EchoFramesAsync, CancellationToken.None);
        using var second = new UnixConnectorSocketServer(SocketPath);

        await Assert.ThrowsAsync<IOException>(() => second.StartAsync(EchoFramesAsync, CancellationToken.None));
    }

    [UnixFact]
    public async Task ConnectorAndControlSockets_ShareADirectoryWithoutSharingALock()
    {
        using var control = new UnixSocketServer(_temp.File("control.sock"));
        await control.StartAsync((_, _) => Task.FromResult("ok"), CancellationToken.None);
        using var connector = new UnixConnectorSocketServer(SocketPath);

        await connector.StartAsync(EchoFramesAsync, CancellationToken.None);

        Assert.True(File.Exists(_temp.File("connector.lock")));
        Assert.True(File.Exists(_temp.File("control.lock")));
    }

    [UnixFact]
    public async Task AddAddress_AcceptsConnectionsWithTheSameHandlerInAPrivateDirectory()
    {
        using var server = new UnixConnectorSocketServer(SocketPath);
        await server.StartAsync(EchoFramesAsync, CancellationToken.None);
        // Short on purpose: macOS caps socket paths at 104 bytes and its temp directory is already long.
        var sandboxPath = _temp.File(Path.Join("app", "fx", "c.sock"));

        await server.AddAddressAsync(sandboxPath, CancellationToken.None);
        await server.AddAddressAsync(sandboxPath, CancellationToken.None);

        await using var stream = await new UnixConnectorSocketClient(sandboxPath).ConnectAsync(s_timeout);
        Assert.NotNull(stream);
        await NativeMessagingFraming.WriteFrameAsync(stream, "\"sandboxed\"", CancellationToken.None);
        Assert.Equal("\"sandboxed\"", await NativeMessagingFraming.ReadFrameAsync(stream, CancellationToken.None).WaitAsync(s_timeout));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(Path.GetDirectoryName(sandboxPath)!)
        );

        await server.StopAsync();

        Assert.False(File.Exists(sandboxPath));
        Assert.False(File.Exists(SocketPath));
    }

    [UnixFact]
    public async Task AddAddress_RequiresAStartedServer()
    {
        using var server = new UnixConnectorSocketServer(SocketPath);

        await Assert.ThrowsAsync<InvalidOperationException>(() => server.AddAddressAsync(_temp.File("extra.sock"), CancellationToken.None));
    }

    [UnixTheory]
    [InlineData("org.mozilla.firefox", "/run/user/1000", "/run/user/1000/app/org.mozilla.firefox/juvin/connector.sock")]
    [InlineData(null, "/run/user/1000", null)]
    [InlineData("org.mozilla.firefox", null, null)]
    [InlineData("../escape", "/run/user/1000", null)]
    [InlineData("a/b", "/run/user/1000", null)]
    public void FlatpakConnectorPath_UsesThePerAppRuntimeDirectory(string? flatpakId, string? runtimeDirectory, string? expected)
    {
        Assert.Equal(expected, SocketPathResolver.FlatpakConnectorPath(flatpakId, runtimeDirectory));
    }

    private static async Task EchoFramesAsync(Stream stream, CancellationToken ct)
    {
        while (await NativeMessagingFraming.ReadFrameAsync(stream, ct) is { } frame)
        {
            await NativeMessagingFraming.WriteFrameAsync(stream, frame, ct);
        }
    }
}
