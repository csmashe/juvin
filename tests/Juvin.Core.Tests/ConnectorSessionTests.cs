using System.Buffers.Binary;
using System.Text.Json;
using Juvin.Core.Connector;
using Juvin.Core.Rewrite;
using Juvin.Core.Tests.Fakes;

namespace Juvin.Core.Tests;

public sealed class ConnectorSessionTests : IAsyncDisposable
{
    private const string Hello =
        """{"v":1,"type":"hello","extension":{"id":"extension@juvin.app","version":"0.1.0"},"browser":{"name":"firefox","version":"157.0"}}""";

    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(5);

    private readonly FakeConnectorChannel _channel = new();
    private readonly Task _loop;
    private readonly ConnectorSession _session;

    public ConnectorSessionTests()
    {
        _session = new ConnectorSession(_channel, () => Config("Ctrl+Shift+F"));
        _loop = _session.RunAsync(CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await _session.DisposeAsync();
        await _loop.WaitAsync(s_timeout);
    }

    [Fact]
    public async Task MessagesBeforeHello_GetHelloRequired()
    {
        _channel.Deliver("""{"v":1,"type":"ping","id":"p1"}""");

        var reply = await _channel.NextSentAsync();

        AssertError(reply, ConnectorErrorCodes.HelloRequired, "p1");
        Assert.Null(_session.Client);
    }

    [Fact]
    public async Task Errors_AreNeverAnsweredWithAnotherError()
    {
        _channel.Deliver("""{"v":1,"type":"error","id":"x1"}""");
        _channel.Deliver("""{"v":1,"type":"error","id":"x2","error":{"code":"bad-message","message":"Bad."}}""");
        _channel.Deliver("""{"v":1,"type":"ping","id":"p1"}""");

        var reply = await _channel.NextSentAsync();

        AssertError(reply, ConnectorErrorCodes.HelloRequired, "p1");
        Assert.False(_channel.HasSent);
    }

    [Fact]
    public async Task Hello_IsAnsweredWithConfigAndRecordsTheClient()
    {
        var helloSeen = new TaskCompletionSource();
        _session.HelloReceived += _ => helloSeen.TrySetResult();

        _channel.Deliver(Hello);

        var reply = await _channel.NextSentAsync();
        Assert.Equal("config", reply.GetProperty("type").GetString());
        Assert.Equal("Ctrl+Shift+F", reply.GetProperty("shortcut").GetString());
        Assert.Equal(JsonValueKind.Null, reply.GetProperty("health").GetProperty("modelReachable").ValueKind);
        await helloSeen.Task.WaitAsync(s_timeout);
        Assert.Equal("extension@juvin.app", _session.Client?.Extension.Id);
        Assert.Equal("firefox", _session.Client?.Browser.Name);
    }

    [Fact]
    public async Task Ping_IsAnsweredWithPong()
    {
        await SayHelloAsync();

        _channel.Deliver("""{"v":1,"type":"ping","id":"p7"}""");

        var reply = await _channel.NextSentAsync();
        Assert.Equal("pong", reply.GetProperty("type").GetString());
        Assert.Equal("p7", reply.GetProperty("id").GetString());
    }

    [Theory]
    [InlineData("""{"v":1,"type":"teleport","id":"t1"}""", "t1")]
    [InlineData("""{"v":1,"type":"context.request","id":"c1"}""", "c1")]
    [InlineData("""{"v":1,"type":"testpage.request","id":"t1"}""", "t1")]
    public async Task UnknownOrWrongDirectionTypes_GetUnknownType(string json, string id)
    {
        await SayHelloAsync();

        _channel.Deliver(json);

        AssertError(await _channel.NextSentAsync(), ConnectorErrorCodes.UnknownType, id);
        Assert.False(_session.IsClosed);
    }

    [Fact]
    public async Task BadMessage_GetsAnErrorAndTheSessionContinues()
    {
        await SayHelloAsync();

        _channel.Deliver("{oops");
        AssertError(await _channel.NextSentAsync(), ConnectorErrorCodes.BadMessage, null);

        _channel.Deliver("""{"v":1,"type":"ping","id":"p2"}""");
        Assert.Equal("pong", (await _channel.NextSentAsync()).GetProperty("type").GetString());
    }

    [Fact]
    public async Task UnsupportedVersion_IsAnsweredThenTheConnectionCloses()
    {
        _channel.Deliver("""{"v":2,"type":"hello","id":"h1"}""");

        AssertError(await _channel.NextSentAsync(), ConnectorErrorCodes.UnsupportedVersion, "h1");
        await _loop.WaitAsync(s_timeout);
        Assert.True(_session.IsClosed);
        Assert.True(_channel.IsDisposed);
    }

    [Fact]
    public async Task ShortcutPressed_RaisesTheEvent()
    {
        await SayHelloAsync();
        var pressed = new TaskCompletionSource<ConnectorSession>();
        _session.ShortcutPressed += session => pressed.TrySetResult(session);

        _channel.Deliver("""{"v":1,"type":"shortcut.pressed"}""");

        Assert.Same(_session, await pressed.Task.WaitAsync(s_timeout));
    }

    [Fact]
    public async Task RequestContext_ReturnsTheCorrelatedCapture()
    {
        await SayHelloAsync();

        var pending = _session.RequestContextAsync(null, s_timeout, CancellationToken.None);
        var request = await _channel.NextSentAsync();
        Assert.Equal("context.request", request.GetProperty("type").GetString());
        Assert.False(request.TryGetProperty("editorId", out _));
        Assert.False(request.TryGetProperty("requireFocus", out _));
        var id = request.GetProperty("id").GetString()!;

        _channel.Deliver("""{"v":1,"type":"context.response","id":"someone-else","ok":false,"error":{"code":"internal","message":"x"}}""");
        _channel.Deliver(CaptureJson(id, "visible-text-only"));
        var result = await pending;

        Assert.True(result.Ok);
        Assert.Equal("Draft text", result.Capture!.Context.Draft);
        Assert.Equal(ThreadCompleteness.VisibleTextOnly, result.Capture.Context.Completeness);
        Assert.Equal("e-1", result.Capture.EditorId);
        Assert.Equal("sha256:ab", result.Capture.EditorHash);
    }

    [Fact]
    public async Task RequestFocusedContext_AsksForAFocusedWindowWithoutAnEditor()
    {
        await SayHelloAsync();

        var pending = _session.RequestFocusedContextAsync(s_timeout, CancellationToken.None);
        var request = await _channel.NextSentAsync();
        Assert.True(request.GetProperty("requireFocus").GetBoolean());
        Assert.False(request.TryGetProperty("editorId", out _));
        _channel.Deliver(
            $$$"""{"v":1,"type":"context.response","id":"{{{request.GetProperty("id").GetString()}}}","ok":false,"error":{"code":"browser-not-focused","message":"Focus."}}"""
        );

        Assert.Equal("browser-not-focused", (await pending).Error?.Code);
    }

    [Fact]
    public async Task RequestContext_WithAnEditorId_SendsIt()
    {
        await SayHelloAsync();

        var pending = _session.RequestContextAsync("e-1", s_timeout, CancellationToken.None);
        var request = await _channel.NextSentAsync();
        Assert.Equal("e-1", request.GetProperty("editorId").GetString());
        _channel.Deliver(
            $$$"""{"v":1,"type":"context.response","id":"{{{request.GetProperty("id").GetString()}}}","ok":false,"error":{"code":"editor-gone","message":"Gone."}}"""
        );

        Assert.Equal("editor-gone", (await pending).Error?.Code);
    }

    [Fact]
    public async Task RequestContext_ReturnsTheExtensionsError()
    {
        await SayHelloAsync();

        var pending = _session.RequestContextAsync(null, s_timeout, CancellationToken.None);
        var id = (await _channel.NextSentAsync()).GetProperty("id").GetString();
        _channel.Deliver(
            $$$"""{"v":1,"type":"context.response","id":"{{{id}}}","ok":false,"error":{"code":"no-selection","message":"Nothing is selected."}}"""
        );
        var result = await pending;

        Assert.False(result.Ok);
        Assert.Equal("no-selection", result.Error?.Code);
    }

    [Fact]
    public async Task ConcurrentRequests_AreMatchedByIdOutOfOrder()
    {
        await SayHelloAsync();

        var apply = _session.ApplyAsync("e-1", "sha256:ab", 2, 5, "New text", null, s_timeout, CancellationToken.None);
        var applyRequest = await _channel.NextSentAsync();
        var ping = _session.PingAsync(s_timeout, CancellationToken.None);
        var pingRequest = await _channel.NextSentAsync();

        Assert.Equal("apply.request", applyRequest.GetProperty("type").GetString());
        Assert.Equal("e-1", applyRequest.GetProperty("editorId").GetString());
        Assert.Equal("sha256:ab", applyRequest.GetProperty("expectedHash").GetString());
        Assert.Equal(2, applyRequest.GetProperty("start").GetInt32());
        Assert.Equal(5, applyRequest.GetProperty("end").GetInt32());
        Assert.Equal("New text", applyRequest.GetProperty("text").GetString());
        Assert.False(applyRequest.TryGetProperty("format", out _));
        var applyId = applyRequest.GetProperty("id").GetString();
        var pingId = pingRequest.GetProperty("id").GetString();
        Assert.NotEqual(applyId, pingId);

        _channel.Deliver($$"""{"v":1,"type":"pong","id":"{{pingId}}"}""");
        _channel.Deliver(
            $$$"""{"v":1,"type":"apply.response","id":"{{{applyId}}}","ok":false,"error":{"code":"draft-changed","message":"Changed."}}"""
        );

        Assert.True(await ping >= TimeSpan.Zero);
        Assert.Equal("draft-changed", (await apply)?.Code);
    }

    [Fact]
    public async Task Request_TimesOutWhileTheSendIsStuck()
    {
        await SayHelloAsync();
        _channel.BlockSends = true;

        await Assert.ThrowsAsync<TimeoutException>(() => _session.PingAsync(TimeSpan.FromMilliseconds(100), CancellationToken.None));
    }

    [Fact]
    public async Task Apply_ReturnsNullOnSuccess()
    {
        await SayHelloAsync();

        var apply = _session.ApplyAsync("e-1", "sha256:ab", 2, 5, "New text", null, s_timeout, CancellationToken.None);
        var id = (await _channel.NextSentAsync()).GetProperty("id").GetString();
        _channel.Deliver($$"""{"v":1,"type":"apply.response","id":"{{id}}","ok":true}""");

        Assert.Null(await apply);
    }

    [Fact]
    public async Task Apply_WithMarkdown_SendsTheMarkdownFormat()
    {
        await SayHelloAsync();

        var apply = _session.ApplyAsync("e-1", "sha256:ab", 2, 5, "**New** text", "sha256:ab12", s_timeout, CancellationToken.None);
        var request = await _channel.NextSentAsync();
        _channel.Deliver($$"""{"v":1,"type":"apply.response","id":"{{request.GetProperty("id").GetString()}}","ok":true}""");

        Assert.Equal("markdown", request.GetProperty("format").GetString());
        Assert.Equal("sha256:ab12", request.GetProperty("expectedFormatHash").GetString());
        Assert.Equal("**New** text", request.GetProperty("text").GetString());
        Assert.Null(await apply);
    }

    [Fact]
    public async Task OpenTestPage_ReturnsNullOnOkAndTheErrorOtherwise()
    {
        await SayHelloAsync();

        var opened = _session.OpenTestPageAsync(s_timeout, CancellationToken.None);
        var request = await _channel.NextSentAsync();
        Assert.Equal("testpage.request", request.GetProperty("type").GetString());
        _channel.Deliver($$"""{"v":1,"type":"testpage.response","id":"{{request.GetProperty("id").GetString()}}","ok":true}""");

        Assert.Null(await opened);

        var failed = _session.OpenTestPageAsync(s_timeout, CancellationToken.None);
        var id = (await _channel.NextSentAsync()).GetProperty("id").GetString();
        _channel.Deliver(
            $$$"""{"v":1,"type":"testpage.response","id":"{{{id}}}","ok":false,"error":{"code":"internal","message":"Could not open."}}"""
        );

        Assert.Equal("internal", (await failed)?.Code);
    }

    [Fact]
    public async Task ErrorReplyToARequest_CompletesIt()
    {
        await SayHelloAsync();

        var ping = _session.PingAsync(s_timeout, CancellationToken.None);
        var id = (await _channel.NextSentAsync()).GetProperty("id").GetString();
        _channel.Deliver($$$"""{"v":1,"type":"error","id":"{{{id}}}","error":{"code":"unknown-type","message":"No."}}""");

        var ex = await Assert.ThrowsAsync<ConnectorException>(() => ping);
        Assert.Equal("unknown-type", ex.Code);
    }

    [Fact]
    public async Task Request_TimesOutWithoutAResponse()
    {
        await SayHelloAsync();

        await Assert.ThrowsAsync<TimeoutException>(() =>
            _session.RequestContextAsync(null, TimeSpan.FromMilliseconds(100), CancellationToken.None)
        );
    }

    [Fact]
    public async Task Disconnect_FailsAPendingRequest()
    {
        await SayHelloAsync();

        var pending = _session.RequestContextAsync(null, s_timeout, CancellationToken.None);
        await _channel.NextSentAsync();
        _channel.Disconnect();

        var ex = await Assert.ThrowsAsync<ConnectorException>(() => pending);
        Assert.Equal(ConnectorErrorCodes.Disconnected, ex.Code);
        await _loop.WaitAsync(s_timeout);
        await Assert.ThrowsAsync<ConnectorException>(() => _session.PingAsync(s_timeout, CancellationToken.None));
    }

    [Fact]
    public async Task Requests_BeforeHelloAreRefused()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _session.RequestContextAsync(null, s_timeout, CancellationToken.None)
        );
    }

    [Fact]
    public async Task SendConfig_SendsTheNewConfig()
    {
        await SayHelloAsync();

        await _session.SendConfigAsync(Config("Ctrl+Alt+J"), CancellationToken.None);

        var sent = await _channel.NextSentAsync();
        Assert.Equal("config", sent.GetProperty("type").GetString());
        Assert.Equal("Ctrl+Alt+J", sent.GetProperty("shortcut").GetString());
    }

    [Fact]
    public async Task OversizedFrame_ClosesTheSession()
    {
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)NativeMessagingFraming.MaxFrameBytes + 1);
        var session = new ConnectorSession(new StreamConnectorChannel(new MemoryStream(header)), () => Config("x"));

        await session.RunAsync(CancellationToken.None).WaitAsync(s_timeout);

        Assert.True(session.IsClosed);
    }

    private async Task SayHelloAsync()
    {
        _channel.Deliver(Hello);
        Assert.Equal("config", (await _channel.NextSentAsync()).GetProperty("type").GetString());
    }

    private static ConnectorConfig Config(string shortcut) =>
        new()
        {
            Actions = [new ConnectorAction("soften", "Soften")],
            Adapters = ["gmail"],
            Shortcut = shortcut,
            Health = new ConnectorHealth("local", null),
        };

    private static string CaptureJson(string id, string completeness) =>
        $$$"""
        {"v":1,"type":"context.response","id":"{{{id}}}","ok":true,"capture":{
          "adapter":"generic","editor":{"id":"e-1","hash":"sha256:ab"},
          "selection":{"text":"Draft text","start":0,"end":10},
          "thread":{"completeness":"{{{completeness}}}","messages":[]}}
        }
        """;

    private static void AssertError(JsonElement reply, string code, string? id)
    {
        Assert.Equal("error", reply.GetProperty("type").GetString());
        Assert.Equal(code, reply.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(id, reply.TryGetProperty("id", out var actual) ? actual.GetString() : null);
    }
}
