using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json;
using Juvin.App.Services;
using Juvin.App.Tests.Fakes;
using Juvin.Core.Connector;
using Juvin.Core.Localization;
using Juvin.Core.Models;
using Juvin.Core.Rewrite;
using Juvin.Platform;

namespace Juvin.App.Tests;

public sealed class ConnectorServiceTests : IAsyncDisposable
{
    private const string Hello =
        """{"v":1,"type":"hello","extension":{"id":"extension@juvin.app","version":"0.1.0"},"browser":{"name":"chrome","version":"140.0"}}""";

    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(5);

    private readonly FakeNativeHostRegistrar _registrar = new();
    private readonly ConnectorService _service;
    private readonly FakeSettingsService _settings = new();
    private readonly FakeConnectorSocket _socket = new();
    private readonly FakeTimeProvider _time = new();

    public ConnectorServiceTests()
    {
        _service = new ConnectorService(_socket, _registrar, _settings, new Localizer(), _time);
    }

    public async ValueTask DisposeAsync()
    {
        await _service.StopAsync();
    }

    [Fact]
    public void BuildConfig_ListsLocalizedActionsAdaptersShortcutAndMode()
    {
        var config = _service.BuildConfig(new AppSettings { ProcessingMode = ProcessingMode.Codex, RewriteShortcut = "Ctrl+Alt+J" });

        Assert.Equal(RewriteAction.BuiltIn.Select(action => action.Id), config.Actions.Select(action => action.Id));
        Assert.Equal("Make clearer", config.Actions.Single(action => action.Id == "clearer").Title);
        Assert.Equal(["gmail", "generic", "test-page"], config.Adapters);
        Assert.Equal("Ctrl+Alt+J", config.Shortcut);
        Assert.Equal(new ConnectorHealth("codex", null), config.Health);

        var custom = _service.BuildConfig(
            new AppSettings { CustomRewriteActions = [new CustomRewriteAction("custom-1", "Pirate voice", "Talk like a pirate.")] }
        );

        Assert.Equal([.. RewriteAction.BuiltIn.Select(action => action.Id), "custom-1"], custom.Actions.Select(action => action.Id));
        Assert.Equal("Pirate voice", custom.Actions[^1].Title);
    }

    [Fact]
    public async Task Start_ListensOnEverySandboxEndpoint()
    {
        _registrar.Endpoints.AddRange(
            [
                new SandboxEndpoint("firefox-flatpak", "/run/user/1000/app/org.mozilla.firefox/juvin/connector.sock"),
                new SandboxEndpoint("zen-flatpak", "/run/user/1000/app/app.zen_browser.zen/juvin/connector.sock"),
            ]
        );

        await _service.StartAsync(CancellationToken.None);

        Assert.Equal(_registrar.Endpoints.Select(endpoint => endpoint.Address), _socket.AddedAddresses);
    }

    [Fact]
    public async Task Start_SurvivesAnEndpointThatCannotBeBound()
    {
        _registrar.Endpoints.Add(new SandboxEndpoint("firefox-flatpak", "/unbindable"));
        _socket.AddAddressFailure = new IOException("in use");

        await _service.StartAsync(CancellationToken.None);

        Assert.Empty(_socket.AddedAddresses);
        Assert.Equal(_registrar.Endpoints, await _service.BindSandboxEndpointsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task BindSandboxEndpoints_ContinuesPastASocketError()
    {
        _registrar.Endpoints.AddRange(
            [
                new SandboxEndpoint("firefox-flatpak", "/too/long"),
                new SandboxEndpoint("zen-flatpak", "/run/user/1000/app/app.zen_browser.zen/juvin/connector.sock"),
            ]
        );
        _socket.AddAddressFailure = new SocketException((int)SocketError.AddressAlreadyInUse);
        _socket.FailOnlyAddress = "/too/long";

        var unbound = await _service.BindSandboxEndpointsAsync(CancellationToken.None);

        Assert.Equal([_registrar.Endpoints[0]], unbound);
        Assert.Equal([_registrar.Endpoints[1].Address], _socket.AddedAddresses);
    }

    [Fact]
    public async Task Hello_GetsConfigAndListsTheClientUntilItDisconnects()
    {
        await _service.StartAsync(CancellationToken.None);
        var changes = 0;
        // ReSharper disable once AccessToModifiedClosure -- the handler is the only writer; the test reads the count after awaiting
        _service.ClientsChanged += () => Interlocked.Increment(ref changes);

        var stream = await ConnectAndSayHelloAsync();

        Assert.Equal("chrome", Assert.Single(_service.Clients).Client?.Browser.Name);
        await stream.DisposeAsync();
        // The session leaves the list before ClientsChanged is raised.
        await WaitUntilAsync(() => _service.Clients.Count == 0 && Volatile.Read(ref changes) == 2);
    }

    [Fact]
    public async Task SettingsChange_ResendsConfigToConnectedClients()
    {
        await _service.StartAsync(CancellationToken.None);
        await using var stream = await ConnectAndSayHelloAsync();

        _settings.Update(settings => settings with { RewriteShortcut = "Ctrl+Alt+K" });

        var config = await ReadAsync(stream);
        Assert.Equal("config", config.GetProperty("type").GetString());
        Assert.Equal("Ctrl+Alt+K", config.GetProperty("shortcut").GetString());
    }

    [Fact]
    public async Task ShortcutPressed_RequestsAndReportsTheCapture()
    {
        await _service.StartAsync(CancellationToken.None);
        var completed = new TaskCompletionSource<ContextResult>();
        _service.CaptureCompleted += (_, _, result) => completed.TrySetResult(result);
        await using var stream = await ConnectAndSayHelloAsync();

        await WriteAsync(stream, """{"v":1,"type":"shortcut.pressed"}""");
        var request = await ReadAsync(stream);
        Assert.Equal("context.request", request.GetProperty("type").GetString());
        await WriteAsync(
            stream,
            $$$"""
            {"v":1,"type":"context.response","id":"{{{request.GetProperty("id").GetString()}}}","ok":true,"capture":{
              "adapter":"test-page","editor":{"id":"e-1","hash":"sha256:ab"},
              "selection":{"text":"Draft","start":0,"end":5},
              "thread":{"completeness":"draft-only","messages":[]}}
            }
            """
        );

        var result = await completed.Task.WaitAsync(s_timeout);
        Assert.Equal("Draft", result.Capture?.Context.Draft);
        Assert.Equal(ThreadCompleteness.DraftOnly, result.Capture?.Context.Completeness);
    }

    [Fact]
    public async Task ShortcutPressed_DiscardsACaptureThatFinishesAfterANewerOne()
    {
        await _service.StartAsync(CancellationToken.None);
        var drafts = new ConcurrentQueue<string?>();
        _service.CaptureCompleted += (_, _, result) => drafts.Enqueue(result.Capture?.Context.Draft);
        await using var stream = await ConnectAndSayHelloAsync();

        await WriteAsync(stream, """{"v":1,"type":"shortcut.pressed"}""");
        var older = (await ReadAsync(stream)).GetProperty("id").GetString()!;
        _time.Advance(TimeSpan.FromSeconds(1));
        await WriteAsync(stream, """{"v":1,"type":"shortcut.pressed"}""");
        var newer = (await ReadAsync(stream)).GetProperty("id").GetString()!;

        await WriteAsync(stream, CaptureResponse(newer, "Newer"));
        await WaitUntilAsync(() => !drafts.IsEmpty);
        await WriteAsync(stream, CaptureResponse(older, "Older"));

        // The pong is read after the older response, which gives that capture time to finish.
        var ping = _service.PingAsync(s_timeout, CancellationToken.None);
        var pingRequest = await ReadAsync(stream);
        await WriteAsync(stream, $$"""{"v":1,"type":"pong","id":"{{pingRequest.GetProperty("id").GetString()}}"}""");
        Assert.NotNull(await ping);
        await Task.Delay(50);

        Assert.Equal(["Newer"], drafts);
    }

    [Fact]
    public async Task TriggerCapture_WithNoBrowser_ReportsNoBrowserWithoutASession()
    {
        await _service.StartAsync(CancellationToken.None);
        ContextResult? failed = null;
        var completed = 0;
        _service.TriggerFailed += (_, result) => failed = result;
        _service.CaptureCompleted += (_, _, _) => completed++;

        await _service.TriggerCaptureAsync();

        Assert.Equal(ConnectorService.NoBrowserCode, failed?.Error?.Code);
        Assert.Equal(0, completed);
    }

    [Fact]
    public async Task TriggerCapture_AsksEveryBrowserAndTakesTheFirstCaptureWithoutWaitingForTheRest()
    {
        await _service.StartAsync(CancellationToken.None);
        var completed = new TaskCompletionSource<(ConnectorSession Session, ContextResult Result)>();
        _service.CaptureCompleted += (client, _, outcome) => completed.TrySetResult((client, outcome));
        await using var silent = await ConnectAndSayHelloAsync();
        await using var answering = await ConnectAndSayHelloAsync();

        var trigger = _service.TriggerCaptureAsync();
        var silentRequest = await ReadAsync(silent);
        var request = await ReadAsync(answering);
        Assert.True(silentRequest.GetProperty("requireFocus").GetBoolean());
        Assert.True(request.GetProperty("requireFocus").GetBoolean());
        await WriteAsync(answering, CaptureResponse(request.GetProperty("id").GetString()!, "Draft"));

        var (session, result) = await completed.Task.WaitAsync(s_timeout);
        Assert.Equal("Draft", result.Capture?.Context.Draft);
        Assert.Same(_service.Clients[1], session);
        await trigger.WaitAsync(s_timeout);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TriggerCapture_PrefersAnErrorOtherThanBrowserNotFocused(bool notFocusedFirst)
    {
        await _service.StartAsync(CancellationToken.None);
        var completed = new TaskCompletionSource<(ConnectorSession Session, ContextResult Result)>();
        _service.CaptureCompleted += (client, _, outcome) => completed.TrySetResult((client, outcome));
        await using var unfocused = await ConnectAndSayHelloAsync();
        await using var focused = await ConnectAndSayHelloAsync();

        _ = _service.TriggerCaptureAsync();
        var unfocusedId = (await ReadAsync(unfocused)).GetProperty("id").GetString()!;
        var focusedId = (await ReadAsync(focused)).GetProperty("id").GetString()!;
        if (notFocusedFirst)
        {
            await WriteAsync(unfocused, ErrorResponse(unfocusedId, ConnectorService.BrowserNotFocusedCode));
            await Task.Delay(50);
            await WriteAsync(focused, ErrorResponse(focusedId, "no-selection"));
        }
        else
        {
            await WriteAsync(focused, ErrorResponse(focusedId, "no-selection"));
            await Task.Delay(50);
            await WriteAsync(unfocused, ErrorResponse(unfocusedId, ConnectorService.BrowserNotFocusedCode));
        }

        var (session, result) = await completed.Task.WaitAsync(s_timeout);
        Assert.Equal("no-selection", result.Error?.Code);
        Assert.Same(_service.Clients[1], session);
    }

    [Fact]
    public async Task TriggerCapture_ReportsBrowserNotFocusedWhenEveryBrowserSaysSo()
    {
        await _service.StartAsync(CancellationToken.None);
        var completed = new TaskCompletionSource<ContextResult>();
        _service.CaptureCompleted += (_, _, result) => completed.TrySetResult(result);
        await using var first = await ConnectAndSayHelloAsync();
        await using var second = await ConnectAndSayHelloAsync();

        _ = _service.TriggerCaptureAsync();
        await WriteAsync(first, ErrorResponse((await ReadAsync(first)).GetProperty("id").GetString()!, ConnectorService.BrowserNotFocusedCode));
        await WriteAsync(second, ErrorResponse((await ReadAsync(second)).GetProperty("id").GetString()!, ConnectorService.BrowserNotFocusedCode));

        Assert.Equal(ConnectorService.BrowserNotFocusedCode, (await completed.Task.WaitAsync(s_timeout)).Error?.Code);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OnePressSeenByTheHotkeyAndTheExtension_CapturesOnce(bool hotkeyFirst)
    {
        await _service.StartAsync(CancellationToken.None);
        var completed = new ConcurrentQueue<long>();
        _service.CaptureCompleted += (_, number, _) => completed.Enqueue(number);
        await using var stream = await ConnectAndSayHelloAsync();

        if (hotkeyFirst)
        {
            _ = _service.TriggerCaptureAsync();
            var request = await ReadAsync(stream);
            _time.Advance(ConnectorService.CoalesceWindow - TimeSpan.FromMilliseconds(1));
            await WriteAsync(stream, """{"v":1,"type":"shortcut.pressed"}""");
            await WriteAsync(stream, CaptureResponse(request.GetProperty("id").GetString()!, "Draft"));
        }
        else
        {
            await WriteAsync(stream, """{"v":1,"type":"shortcut.pressed"}""");
            var request = await ReadAsync(stream);
            _time.Advance(ConnectorService.CoalesceWindow - TimeSpan.FromMilliseconds(1));
            await _service.TriggerCaptureAsync();
            await WriteAsync(stream, CaptureResponse(request.GetProperty("id").GetString()!, "Draft"));
        }

        await WaitUntilAsync(() => !completed.IsEmpty);
        await AssertNoRequestPendingAsync(stream);
        Assert.Equal([1L], completed);
    }

    [Fact]
    public async Task TriggerWithNoBrowser_DoesNotSwallowAShortcutPressThatFollows()
    {
        await _service.StartAsync(CancellationToken.None);
        var failed = new TaskCompletionSource<ContextResult>();
        _service.TriggerFailed += (_, result) => failed.TrySetResult(result);
        var completed = new TaskCompletionSource<ContextResult>();
        _service.CaptureCompleted += (_, _, result) => completed.TrySetResult(result);

        await _service.TriggerCaptureAsync();
        Assert.Equal(ConnectorService.NoBrowserCode, (await failed.Task.WaitAsync(s_timeout)).Error?.Code);

        await using var stream = await ConnectAndSayHelloAsync();
        _time.Advance(TimeSpan.FromMilliseconds(1));
        await WriteAsync(stream, """{"v":1,"type":"shortcut.pressed"}""");
        var request = await ReadAsync(stream);
        await WriteAsync(stream, CaptureResponse(request.GetProperty("id").GetString()!, "Draft"));

        Assert.True((await completed.Task.WaitAsync(s_timeout)).Ok);
    }

    [Fact]
    public async Task PressesASecondApart_BothCapture()
    {
        await _service.StartAsync(CancellationToken.None);
        await using var stream = await ConnectAndSayHelloAsync();

        await WriteAsync(stream, """{"v":1,"type":"shortcut.pressed"}""");
        Assert.Equal("context.request", (await ReadAsync(stream)).GetProperty("type").GetString());
        _time.Advance(TimeSpan.FromSeconds(1));
        _ = _service.TriggerCaptureAsync();

        Assert.True((await ReadAsync(stream)).GetProperty("requireFocus").GetBoolean());
    }

    [Fact]
    public async Task Ping_ReturnsLatencyFromTheConnectedClient()
    {
        await _service.StartAsync(CancellationToken.None);
        Assert.Null(await _service.PingAsync(s_timeout, CancellationToken.None));
        await using var stream = await ConnectAndSayHelloAsync();

        var ping = _service.PingAsync(s_timeout, CancellationToken.None);
        var request = await ReadAsync(stream);
        await WriteAsync(stream, $$"""{"v":1,"type":"pong","id":"{{request.GetProperty("id").GetString()}}"}""");

        Assert.NotNull(await ping);
    }

    [Fact]
    public async Task OpenTestPage_WithNoBrowser_ReturnsNoBrowser()
    {
        await _service.StartAsync(CancellationToken.None);

        var error = await _service.OpenTestPageAsync(s_timeout, CancellationToken.None);

        Assert.Equal(ConnectorService.NoBrowserCode, error?.Code);
    }

    [Fact]
    public async Task OpenTestPage_AsksTheConnectedClientAndReturnsItsAnswer()
    {
        await _service.StartAsync(CancellationToken.None);
        await using var stream = await ConnectAndSayHelloAsync();

        var opened = _service.OpenTestPageAsync(s_timeout, CancellationToken.None);
        var request = await ReadAsync(stream);
        Assert.Equal("testpage.request", request.GetProperty("type").GetString());
        await WriteAsync(stream, $$"""{"v":1,"type":"testpage.response","id":"{{request.GetProperty("id").GetString()}}","ok":true}""");
        Assert.Null(await opened);

        var failed = _service.OpenTestPageAsync(s_timeout, CancellationToken.None);
        var id = (await ReadAsync(stream)).GetProperty("id").GetString();
        await WriteAsync(
            stream,
            $$$"""{"v":1,"type":"testpage.response","id":"{{{id}}}","ok":false,"error":{"code":"internal","message":"Could not open."}}"""
        );
        Assert.Equal("internal", (await failed)?.Code);
    }

    private async Task<Stream> ConnectAndSayHelloAsync()
    {
        var connected = _service.Clients.Count;
        var stream = await _socket.ConnectAsync(s_timeout);
        Assert.NotNull(stream);
        await WriteAsync(stream, Hello);
        Assert.Equal("config", (await ReadAsync(stream)).GetProperty("type").GetString());
        await WaitUntilAsync(() => _service.Clients.Count > connected);
        return stream;
    }

    /// <summary>A ping is answered in order, so a pong as the next frame means no other request was sent.</summary>
    private async Task AssertNoRequestPendingAsync(Stream stream)
    {
        var ping = _service.PingAsync(s_timeout, CancellationToken.None);
        var next = await ReadAsync(stream);
        Assert.Equal("ping", next.GetProperty("type").GetString());
        await WriteAsync(stream, $$"""{"v":1,"type":"pong","id":"{{next.GetProperty("id").GetString()}}"}""");
        Assert.NotNull(await ping);
    }

    private static string ErrorResponse(string id, string code) =>
        $$$"""{"v":1,"type":"context.response","id":"{{{id}}}","ok":false,"error":{"code":"{{{code}}}","message":"Declined."}}""";

    private static string CaptureResponse(string id, string draft) =>
        $$$"""
        {"v":1,"type":"context.response","id":"{{{id}}}","ok":true,"capture":{
          "adapter":"test-page","editor":{"id":"e-1","hash":"sha256:ab"},
          "selection":{"text":"{{{draft}}}","start":0,"end":{{{draft.Length}}}},
          "thread":{"completeness":"draft-only","messages":[]}}
        }
        """;

    private static Task WriteAsync(Stream stream, string json) =>
        NativeMessagingFraming.WriteFrameAsync(stream, json, CancellationToken.None);

    private static async Task<JsonElement> ReadAsync(Stream stream)
    {
        var json = await NativeMessagingFraming.ReadFrameAsync(stream, CancellationToken.None).WaitAsync(s_timeout);
        Assert.NotNull(json);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + s_timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the condition.");
            await Task.Delay(10);
        }
    }
}
