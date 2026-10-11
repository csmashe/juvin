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

    public ConnectorServiceTests()
    {
        _service = new ConnectorService(_socket, _registrar, _settings, new Localizer());
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

    private async Task<Stream> ConnectAndSayHelloAsync()
    {
        var stream = await _socket.ConnectAsync(s_timeout);
        Assert.NotNull(stream);
        await WriteAsync(stream, Hello);
        Assert.Equal("config", (await ReadAsync(stream)).GetProperty("type").GetString());
        await WaitUntilAsync(() => _service.Clients.Count > 0);
        return stream;
    }

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
