using System.Runtime.Versioning;
using Juvin.Platform.Windows;
using Juvin.Platform.Windows.Ipc;
using Juvin.Platform.Windows.Secrets;

namespace Juvin.Platform.Tests;

[SupportedOSPlatform("windows")]
public sealed class WindowsPlatformTests : IDisposable
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(5);

    private readonly string _pipeName = "juvin-test-" + Guid.NewGuid().ToString("N");
    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        _temp.Dispose();
    }

    [WindowsFact]
    public async Task Pipe_SendAsyncReceivesTheHandlersReply()
    {
        using var server = new NamedPipeServer(_pipeName);
        await server.StartAsync((line, _) => Task.FromResult("echo:" + line), CancellationToken.None);
        var client = new NamedPipeClient(_pipeName);

        Assert.Equal("echo:one", await client.SendAsync("one", s_timeout));
        Assert.Equal("echo:two", await client.SendAsync("two", s_timeout));
    }

    [WindowsFact]
    public async Task Pipe_RepliesLineTooLongWhenTheCapIsExceeded()
    {
        using var server = new NamedPipeServer(_pipeName);
        await server.StartAsync((_, _) => Task.FromResult("ok"), CancellationToken.None);

        await using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", _pipeName, System.IO.Pipes.PipeDirection.InOut);
        await pipe.ConnectAsync(5000);
        await pipe.WriteAsync(Enumerable.Repeat((byte)'a', LocalSocketProtocol.MaxLineBytes + 10).ToArray());
        var (reply, _) = await LocalSocketProtocol.ReadLineAsync(pipe, LocalSocketProtocol.MaxLineBytes, CancellationToken.None);

        Assert.Contains(LocalSocketProtocol.ErrLineTooLong, reply);
    }

    [WindowsFact]
    public async Task Pipe_ClientReportsNoPeerWhenNothingIsListening()
    {
        var client = new NamedPipeClient(_pipeName);

        Assert.False(client.IsLivePeer());
        Assert.Null(await client.SendAsync("{}", TimeSpan.FromMilliseconds(300)));
    }

    [WindowsFact]
    public async Task Pipe_SecondServerFailsWhileTheFirstIsRunning()
    {
        using var first = new NamedPipeServer(_pipeName);
        await first.StartAsync((_, _) => Task.FromResult("ok"), CancellationToken.None);
        using var second = new NamedPipeServer(_pipeName);

        Assert.True(new NamedPipeClient(_pipeName).IsLivePeer());
        await Assert.ThrowsAsync<IOException>(() => second.StartAsync((_, _) => Task.FromResult("ok"), CancellationToken.None));
    }

    [WindowsFact]
    public void Guard_AllowsOneHolder()
    {
        var name = @"Local\Juvin.Test." + Guid.NewGuid().ToString("N");
        using var other = new WindowsSingleInstanceGuard(name);
        using (var guard = new WindowsSingleInstanceGuard(name))
        {
            Assert.True(guard.TryAcquire());
            Assert.False(other.TryAcquire());
        }

        Assert.True(other.TryAcquire());
    }

    [WindowsFact]
    public async Task Dpapi_RoundTripsAndDeletes()
    {
        var store = new DpapiSecretStore(_temp.File("Secrets"));

        Assert.Null(await store.GetAsync("token"));
        await store.SetAsync("token", "value");
        Assert.Equal("value", await new DpapiSecretStore(_temp.File("Secrets")).GetAsync("token"));

        await store.DeleteAsync("token");
        await store.DeleteAsync("token");
        Assert.Null(await store.GetAsync("token"));
    }
}
