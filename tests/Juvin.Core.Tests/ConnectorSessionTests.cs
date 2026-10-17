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

    // Letters, spaces, and a control character (written as a JSON escape): never a listed code.
    private const string SecretCode = "Dear Ana, see the draft\\u0007";

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
    public async Task ErrorBeforeHello_TracesAnUnlistedCodeAsUnknown()
    {
        using var trace = new TraceRecorder();

        _channel.Deliver($$$"""{"v":1,"type":"error","error":{"code":"{{{SecretCode}}}","message":"Bad."}}""");
        _channel.Deliver("""{"v":1,"type":"ping","id":"p1"}""");
        AssertError(await _channel.NextSentAsync(), ConnectorErrorCodes.HelloRequired, "p1");

        Assert.Contains(trace.Lines, line => line.Contains("Ignored an error before hello (unknown)."));
        AssertNoSecret(trace);
    }

    [Fact]
    public async Task UnmatchedError_TracesAnUnlistedCodeAsUnknownAndAListedOneAsIs()
    {
        await SayHelloAsync();
        using var trace = new TraceRecorder();

        _channel.Deliver($$$"""{"v":1,"type":"error","id":"x1","error":{"code":"{{{SecretCode}}}","message":"Bad."}}""");
        _channel.Deliver("""{"v":1,"type":"error","id":"x2","error":{"code":"editor-gone","message":"Gone."}}""");
        await RoundTripAsync();

        Assert.Contains(trace.Lines, line => line.Contains("Ignored an unmatched error (error unknown)."));
        Assert.Contains(trace.Lines, line => line.Contains("Ignored an unmatched error (error editor-gone)."));
        AssertNoSecret(trace);
    }

    [Fact]
    public async Task LateResponse_AfterATimeout_IsIgnoredAndTracedByTypeOnly()
    {
        await SayHelloAsync();
        using var trace = new TraceRecorder();

        var request = _session.RequestContextAsync(null, TimeSpan.FromMilliseconds(100), CancellationToken.None);
        var id = (await _channel.NextSentAsync()).GetProperty("id").GetString()!;
        await Assert.ThrowsAsync<TimeoutException>(() => request);
        _channel.Deliver(CaptureJson(id, "entire-thread"));
        await RoundTripAsync();

        Assert.Contains(trace.Lines, line => line.Contains("Ignored an unmatched context.response (error -)."));
        Assert.DoesNotContain(trace.Lines, line => line.Contains("Draft text"));
        Assert.False(_session.IsClosed);
    }

    [Fact]
    public async Task DuplicateResponse_ForACompletedRequest_IsIgnoredAndTracedByTypeOnly()
    {
        await SayHelloAsync();
        using var trace = new TraceRecorder();

        var ping = _session.PingAsync(s_timeout, CancellationToken.None);
        var id = (await _channel.NextSentAsync()).GetProperty("id").GetString();
        _channel.Deliver($$"""{"v":1,"type":"pong","id":"{{id}}"}""");
        await ping;
        _channel.Deliver($$"""{"v":1,"type":"pong","id":"{{id}}"}""");
        await RoundTripAsync();

        Assert.Single(trace.Lines, line => line.Contains("[ConnectorSession] Ignored an unmatched pong (error -)."));
        Assert.False(_session.IsClosed);
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
    [InlineData(
        """{"v":1,"type":"menu.show","id":"m1","menuId":"menu-1","editorId":"e-1","slots":[{"id":"a","title":"A"},{"id":"b","title":"B"},{"id":"c","title":"C"},{"id":"d","title":"D"},{"id":"e","title":"E"}],"more":[],"status":[]}""",
        "m1"
    )]
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
    public async Task ErrorWithAnUnsupportedVersion_ClosesTheConnectionWithoutAReply()
    {
        _channel.Deliver("""{"v":2,"type":"error","error":{"code":"bad-message","message":"Bad."}}""");

        await _loop.WaitAsync(s_timeout);
        Assert.True(_session.IsClosed);
        Assert.False(_channel.HasSent);
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
    public async Task ShowMenu_SendsTheMenuWithItsOwnIdAndReturnsNullOnOk()
    {
        await SayHelloAsync();

        var shown = _session.ShowMenuAsync(Menu() with { Id = "ignored" }, s_timeout, CancellationToken.None);
        var request = await _channel.NextSentAsync();
        Assert.Equal("menu.show", request.GetProperty("type").GetString());
        Assert.Equal("menu-1", request.GetProperty("menuId").GetString());
        Assert.Equal("e-1", request.GetProperty("editorId").GetString());
        Assert.Equal(5, request.GetProperty("slots").GetArrayLength());
        var id = request.GetProperty("id").GetString()!;
        Assert.StartsWith("m", id);
        Assert.NotEqual("ignored", id);
        _channel.Deliver($$"""{"v":1,"type":"menu.response","id":"{{id}}","ok":true}""");

        Assert.Null(await shown);
    }

    [Theory]
    [InlineData("""{"v":1,"type":"menu.response","id":"ID","ok":false,"error":{"code":"unsupported","message":"No menu."}}""", "unsupported")]
    [InlineData("""{"v":1,"type":"error","id":"ID","error":{"code":"unknown-type","message":"Unknown message type."}}""", "unknown-type")]
    public async Task ShowMenu_ReturnsTheExtensionsError(string reply, string code)
    {
        await SayHelloAsync();

        var shown = _session.ShowMenuAsync(Menu(), s_timeout, CancellationToken.None);
        var id = (await _channel.NextSentAsync()).GetProperty("id").GetString()!;
        _channel.Deliver(reply.Replace("ID", id));

        Assert.Equal(code, (await shown)?.Code);
    }

    [Fact]
    public async Task MenuChooseAndClose_RaiseTheirEvents()
    {
        await SayHelloAsync();
        var chosen = new TaskCompletionSource<MenuChooseMessage>();
        var closed = new TaskCompletionSource<MenuCloseMessage>();
        _session.MenuChosen += (_, choose) => chosen.TrySetResult(choose);
        _session.MenuClosed += (_, close) => closed.TrySetResult(close);

        _channel.Deliver("""{"v":1,"type":"menu.choose","menuId":"menu-1","action":"custom","instruction":"Less formal."}""");
        _channel.Deliver("""{"v":1,"type":"menu.close","menuId":"menu-1"}""");

        var choice = await chosen.Task.WaitAsync(s_timeout);
        Assert.Equal(("menu-1", "custom", "Less formal."), (choice.MenuId, choice.Action, choice.Instruction));
        Assert.Equal("menu-1", (await closed.Task.WaitAsync(s_timeout)).MenuId);
        Assert.False(_channel.HasSent);
    }

    [Fact]
    public async Task MenuUpdateAndClose_AreSentAsNotifications()
    {
        await SayHelloAsync();

        await _session.SendMenuUpdateAsync(
            new MenuUpdateMessage { MenuId = "menu-1", State = MenuStates.Error, Text = "Failed.", CanRetry = false },
            CancellationToken.None
        );
        await _session.SendMenuCloseAsync(new MenuCloseMessage { MenuId = "menu-1" }, CancellationToken.None);

        var update = await _channel.NextSentAsync();
        Assert.Equal("menu.update", update.GetProperty("type").GetString());
        Assert.False(update.TryGetProperty("id", out _));
        Assert.Equal("error", update.GetProperty("state").GetString());
        Assert.False(update.GetProperty("canRetry").GetBoolean());
        var close = await _channel.NextSentAsync();
        Assert.Equal("menu.close", close.GetProperty("type").GetString());
        Assert.Equal("menu-1", close.GetProperty("menuId").GetString());
    }

    [Fact]
    public async Task MenuClose_AfterTheConnectionClosed_Throws()
    {
        await SayHelloAsync();
        _channel.Disconnect();
        await _loop.WaitAsync(s_timeout);

        var ex = await Assert.ThrowsAsync<ConnectorException>(() =>
            _session.SendMenuCloseAsync(new MenuCloseMessage { MenuId = "menu-1" }, CancellationToken.None)
        );
        Assert.Equal(ConnectorErrorCodes.Disconnected, ex.Code);
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
    public async Task NoHelloInTime_ClosesTheSession()
    {
        var channel = new FakeConnectorChannel();
        await using var session = new ConnectorSession(channel, () => Config("x"), TimeSpan.FromMilliseconds(200));

        await session.RunAsync(CancellationToken.None).WaitAsync(s_timeout);

        Assert.True(session.IsClosed);
        Assert.True(channel.IsDisposed);
    }

    [Fact]
    public async Task HelloInTime_KeepsTheSessionOpenPastTheDeadline()
    {
        var channel = new FakeConnectorChannel();
        var session = new ConnectorSession(channel, () => Config("x"), TimeSpan.FromMilliseconds(200));
        var loop = session.RunAsync(CancellationToken.None);

        channel.Deliver(Hello);
        Assert.Equal("config", (await channel.NextSentAsync()).GetProperty("type").GetString());
        await Task.Delay(500);
        channel.Deliver("""{"v":1,"type":"ping","id":"p1"}""");

        Assert.Equal("pong", (await channel.NextSentAsync()).GetProperty("type").GetString());
        Assert.False(session.IsClosed);
        await session.DisposeAsync();
        await loop.WaitAsync(s_timeout);
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

    /// <summary>Waits until the session has handled everything delivered before it.</summary>
    private async Task RoundTripAsync()
    {
        _channel.Deliver("""{"v":1,"type":"ping","id":"sync"}""");
        Assert.Equal("sync", (await _channel.NextSentAsync()).GetProperty("id").GetString());
    }

    private static void AssertNoSecret(TraceRecorder trace)
    {
        Assert.DoesNotContain(trace.Lines, line => line.Contains("Dear Ana") || line.Contains('\a'));
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

    private static MenuShowMessage Menu() =>
        new()
        {
            MenuId = "menu-1",
            EditorId = "e-1",
            Slots = [.. "abcde".Select(c => new ConnectorAction(c.ToString(), c.ToString()))],
            More = [],
            Status = ["1 message"],
        };

    private static void AssertError(JsonElement reply, string code, string? id)
    {
        Assert.Equal("error", reply.GetProperty("type").GetString());
        Assert.Equal(code, reply.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(id, reply.TryGetProperty("id", out var actual) ? actual.GetString() : null);
    }
}
