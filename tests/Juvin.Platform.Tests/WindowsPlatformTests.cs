using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Juvin.Core.Connector;
using Juvin.Platform.Windows;
using Juvin.Platform.Windows.Ipc;
using Juvin.Platform.Windows.Secrets;

namespace Juvin.Platform.Tests;

// The pipe tests need an interactive logon: an SSH session is a network logon, which the pipes' NETWORK deny rule blocks.
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

        await using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut);
        await pipe.ConnectAsync(5000);
        await pipe.WriteAsync(Enumerable.Repeat((byte)'a', LocalSocketProtocol.MaxLineBytes + 10).ToArray());
        var (reply, _) = await LocalSocketProtocol.ReadLineAsync(pipe, LocalSocketProtocol.MaxLineBytes, CancellationToken.None);

        Assert.Contains(LocalSocketProtocol.ErrLineTooLong, reply);
    }

    [WindowsFact]
    public async Task Pipe_ClientReportsNoPeerWhenNothingIsListening()
    {
        Assert.Null(await new NamedPipeClient(_pipeName).SendAsync("{}", TimeSpan.FromMilliseconds(300)));
    }

    [WindowsFact]
    public async Task Pipe_SecondServerFailsWhileTheFirstIsRunning()
    {
        using var first = new NamedPipeServer(_pipeName);
        await first.StartAsync((_, _) => Task.FromResult("ok"), CancellationToken.None);
        using var second = new NamedPipeServer(_pipeName);

        Assert.Equal("ok", await new NamedPipeClient(_pipeName).SendAsync("{}", s_timeout));
        await Assert.ThrowsAsync<IOException>(() => second.StartAsync((_, _) => Task.FromResult("ok"), CancellationToken.None));
    }

    [WindowsFact]
    public async Task ConnectorPipe_CarriesFramesBothWaysIncludingLargeOnes()
    {
        using var server = new NamedPipeConnectorServer(_pipeName);
        await server.StartAsync(
            async (stream, ct) =>
            {
                while (await NativeMessagingFraming.ReadFrameAsync(stream, ct) is { } frame)
                {
                    await NativeMessagingFraming.WriteFrameAsync(stream, frame, ct);
                }
            },
            CancellationToken.None
        );
        var large = "\"" + new string('x', 300 * 1024) + "\"";

        await using var stream = await new NamedPipeConnectorClient(_pipeName).ConnectAsync(s_timeout);
        Assert.NotNull(stream);
        await NativeMessagingFraming.WriteFrameAsync(stream, large, CancellationToken.None);
        await NativeMessagingFraming.WriteFrameAsync(stream, "1", CancellationToken.None);

        Assert.Equal(large, await NativeMessagingFraming.ReadFrameAsync(stream, CancellationToken.None).WaitAsync(s_timeout));
        Assert.Equal("1", await NativeMessagingFraming.ReadFrameAsync(stream, CancellationToken.None).WaitAsync(s_timeout));
    }

    [WindowsFact]
    public async Task ConnectorPipe_StopAsyncClosesOpenConnections()
    {
        var server = new NamedPipeConnectorServer(_pipeName);
        await server.StartAsync(
            async (stream, ct) =>
            {
                while (await NativeMessagingFraming.ReadFrameAsync(stream, ct) is not null)
                {
                    // Consume frames until the client closes.
                }
            },
            CancellationToken.None
        );

        await using var stream = await new NamedPipeConnectorClient(_pipeName).ConnectAsync(s_timeout);
        Assert.NotNull(stream);
        await server.StopAsync();

        Assert.Null(await NativeMessagingFraming.ReadFrameAsync(stream, CancellationToken.None).WaitAsync(s_timeout));
    }

    [WindowsFact]
    public async Task ConnectorPipe_StopAsyncClosesConnectionsWhoseHandlerIgnoresCancellation()
    {
        var server = new NamedPipeConnectorServer(_pipeName);
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        await server.StartAsync(
            async (_, _) =>
            {
                entered.SetResult();
                await release.Task;
            },
            CancellationToken.None
        );

        try
        {
            await using var stream = await new NamedPipeConnectorClient(_pipeName).ConnectAsync(s_timeout);
            Assert.NotNull(stream);
            // ReSharper disable once MethodSupportsCancellation -- no token is in scope; the timeout bounds the wait
            await entered.Task.WaitAsync(s_timeout);
            await server.StopAsync();

            Assert.Null(await NativeMessagingFraming.ReadFrameAsync(stream, CancellationToken.None).WaitAsync(s_timeout));
        }
        finally
        {
            release.SetResult();
        }
    }

    [WindowsFact]
    public void Pipe_DeniesNetworkLogons()
    {
        using var pipe = new NamedPipeListener(_pipeName, "test pipe", 4096).CreatePipe(firstInstance: true);
        var network = new SecurityIdentifier(WellKnownSidType.NetworkSid, null);

        var rules = pipe.GetAccessControl().GetAccessRules(true, false, typeof(SecurityIdentifier)).OfType<PipeAccessRule>();

        Assert.Contains(rules, r => r.AccessControlType == AccessControlType.Deny && network.Equals(r.IdentityReference));
    }

    [WindowsFact]
    public async Task ConnectorPipe_ClientReturnsNullWhenNothingIsListening()
    {
        Assert.Null(await new NamedPipeConnectorClient(_pipeName).ConnectAsync(TimeSpan.FromMilliseconds(300)));
    }

    [WindowsFact]
    public async Task ConnectorPipe_HasNoExtraAddresses()
    {
        using var server = new NamedPipeConnectorServer(_pipeName);

        await Assert.ThrowsAsync<NotSupportedException>(() => server.AddAddressAsync("other", CancellationToken.None));
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
