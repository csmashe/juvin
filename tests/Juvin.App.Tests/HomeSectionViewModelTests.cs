using System.Text.Json;
using Juvin.App.Services;
using Juvin.App.Tests.Fakes;
using Juvin.App.ViewModels;
using Juvin.App.ViewModels.Sections;
using Juvin.Core.Connector;
using Juvin.Core.Localization;
using Juvin.Core.Models;
using Juvin.Platform.Hotkeys;

namespace Juvin.App.Tests;

public sealed class HomeSectionViewModelTests : IAsyncDisposable
{
    private const string Hello =
        """{"v":1,"type":"hello","extension":{"id":"extension@juvin.app","version":"0.1.0"},"browser":{"name":"chrome","version":"140.0"}}""";

    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(5);

    private readonly ConnectorService _connector;
    private readonly FakeHistoryService _history = new();
    private readonly Localizer _localizer = new();
    private readonly FakeProviders _providers = new();
    private readonly FakeSettingsService _settings = new();
    private readonly FakeConnectorSocket _socket = new();

    public HomeSectionViewModelTests()
    {
        _connector = new ConnectorService(_socket, new FakeNativeHostRegistrar(), _settings, _localizer);
    }

    public async ValueTask DisposeAsync()
    {
        await _connector.StopAsync();
    }

    [Fact]
    public void TheList_ShowsTheFiveNewest()
    {
        for (var i = 1; i <= 7; i++)
        {
            _history.Add(Entry(i.ToString()));
        }

        var vm = Create();

        Assert.Equal(["7", "6", "5", "4", "3"], vm.RecentRewrites.Select(row => row.Id));
        Assert.True(vm.HasRecentRewrites);
    }

    [Fact]
    public void ARow_ShowsItsEntry()
    {
        _history.Add(Entry("a", subject: null, applied: true));

        var row = Assert.Single(Create().RecentRewrites);

        Assert.Equal("Make clearer", row.Title);
        Assert.Equal("(No subject)", row.Subject);
        Assert.EndsWith(", Gmail", row.Detail);
        Assert.True(row.Applied);
        Assert.Equal("result a", row.Result);
    }

    [Fact]
    public void TheList_FollowsHistoryChanges()
    {
        var vm = Create();
        Assert.False(vm.HasRecentRewrites);

        _history.Add(Entry("a"));

        Assert.Equal("a", Assert.Single(vm.RecentRewrites).Id);
    }

    [Fact]
    public void Delete_RemovesTheEntry()
    {
        _history.Add(Entry("a"));
        _history.Add(Entry("b"));
        var vm = Create();

        vm.DeleteCommand.Execute(vm.RecentRewrites[0]);

        Assert.Equal(["a"], _history.Entries.Select(e => e.Id));
        Assert.Equal(["a"], vm.RecentRewrites.Select(row => row.Id));
    }

    [Fact]
    public void AFailedDelete_ShowsTheError()
    {
        _history.Add(Entry("a"));
        var vm = Create();
        _history.Failure = new IOException("locked");

        vm.DeleteCommand.Execute(vm.RecentRewrites[0]);

        Assert.True(vm.HasError);
        Assert.Single(vm.RecentRewrites);
    }

    [Fact]
    public async Task Copy_WritesTheResult()
    {
        _history.Add(Entry("a"));
        var vm = Create();
        var copied = new List<string>();
        vm.CopyToClipboard = text =>
        {
            copied.Add(text);
            return Task.CompletedTask;
        };

        await vm.CopyCommand.ExecuteAsync(vm.RecentRewrites[0]);

        Assert.Equal(["result a"], copied);
    }

    [Fact]
    public void TheEmptyHint_DependsOnWhetherHistoryIsOn()
    {
        var vm = Create();
        Assert.Equal(_localizer["Home.RecentHint"], vm.RecentHint);

        _settings.Update(s => s with { HistoryEnabled = true });

        Assert.Equal(_localizer["Home.RecentEmpty"], vm.RecentHint);
    }

    [Fact]
    public async Task ConnectorStatus_FollowsConnectedExtensions()
    {
        await _connector.StartAsync(CancellationToken.None);
        var vm = Create();
        Assert.Equal("Not connected", vm.ConnectorStatus);

        var stream = await ConnectAndSayHelloAsync();

        await WaitUntilAsync(() => vm.ConnectorStatus == "Connected: Chrome");
        await stream.DisposeAsync();
        await WaitUntilAsync(() => _connector.Clients.Count == 0 && vm.ConnectorStatus == "Not connected");
    }

    [Theory]
    [InlineData("http://localhost:11434", "qwen3.5:27b", "This device: qwen3.5:27b")]
    [InlineData("http://192.168.1.20:11434", "qwen3.5:27b", "Private server: qwen3.5:27b")]
    [InlineData("http://localhost:11434", null, "This device, no model chosen")]
    [InlineData("not a url", "qwen3.5:27b", "qwen3.5:27b")]
    [InlineData("not a url", null, "No model chosen")]
    [InlineData(null, "qwen3.5:27b", "qwen3.5:27b")]
    public void ModelStatus_InLocalMode_NamesTheServerAndModel(string? url, string? model, string expected)
    {
        // A hand-edited settings.json can hold "localServerUrl": null.
        _settings.Update(s => s with { LocalServerUrl = url!, LocalModel = model });

        Assert.Equal(expected, Create().ModelStatus);
    }

    [Theory]
    [InlineData(null, "Codex")]
    [InlineData("gpt-5", "Codex: gpt-5")]
    public void ModelStatus_InCodexMode_NamesTheCodexModel(string? model, string expected)
    {
        _settings.Update(s => s with { ProcessingMode = ProcessingMode.Codex, CodexModel = model });

        Assert.Equal(expected, Create().ModelStatus);
    }

    [Fact]
    public void ModelStatus_FollowsSettingsChanges()
    {
        var vm = Create();

        _settings.Update(s => s with { LocalServerUrl = "http://localhost:11434", LocalModel = "qwen3:8b" });

        Assert.Equal("This device: qwen3:8b", vm.ModelStatus);
    }

    [Fact]
    public void ShortcutStatus_WithNoBackend_IsNotListening()
    {
        _settings.Update(s => s with { RewriteShortcut = "Ctrl+Shift+F" });

        Assert.Equal("Ctrl+Shift+F, not listening", Create().ShortcutStatus);
    }

    [Fact]
    public async Task ShortcutStatus_FollowsTheHotkeyService()
    {
        _settings.Update(s => s with { RewriteShortcut = "Ctrl+Shift+F" });
        var hotkeys = Hotkeys(new FakeHotkeyBackend("evdev"));
        var vm = Create(hotkeys);

        await hotkeys.StartAsync(CancellationToken.None);

        Assert.Equal("Ctrl+Shift+F, listening", vm.ShortcutStatus);
    }

    [Fact]
    public void ShortcutStatus_FollowsTheShortcutSetting()
    {
        var vm = Create();

        _settings.Update(s => s with { RewriteShortcut = "Ctrl+Alt+J" });

        Assert.Equal("Ctrl+Alt+J, not listening", vm.ShortcutStatus);
    }

    [Theory]
    [InlineData("firefox", "Firefox")]
    [InlineData("chrome", "Chrome")]
    [InlineData("edge", "Edge")]
    [InlineData("chromium", "Chromium")]
    [InlineData("unknown", "Browser")]
    [InlineData("opera", "Browser")]
    public void BrowserLabels_NameTheBrowser(string name, string expected)
    {
        Assert.Equal(expected, BrowserLabels.Describe(name, _localizer));
    }

    private HomeSectionViewModel Create(HotkeyService? hotkeys = null) =>
        new(
            _history,
            _settings,
            _connector,
            hotkeys ?? Hotkeys(),
            _providers.CreateService(_localizer),
            _localizer,
            action => action(),
            isMac: false
        );

    private HotkeyService Hotkeys(params IHotkeyBackend[] backends) => new(backends, _settings, _connector);

    private async Task<Stream> ConnectAndSayHelloAsync()
    {
        var connected = _connector.Clients.Count;
        var stream = await _socket.ConnectAsync(s_timeout);
        Assert.NotNull(stream);
        await NativeMessagingFraming.WriteFrameAsync(stream, Hello, CancellationToken.None);
        var json = await NativeMessagingFraming.ReadFrameAsync(stream, CancellationToken.None).WaitAsync(s_timeout);
        Assert.NotNull(json);
        using var config = JsonDocument.Parse(json);
        Assert.Equal("config", config.RootElement.GetProperty("type").GetString());
        await WaitUntilAsync(() => _connector.Clients.Count > connected);
        return stream;
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

    private static HistoryEntry Entry(string id, string? subject = "Re: timeline", bool applied = false) =>
        new()
        {
            Id = id,
            CreatedAt = DateTimeOffset.UtcNow,
            ActionTitle = "Make clearer",
            Subject = subject,
            Adapter = "gmail",
            Draft = "draft " + id,
            Result = "result " + id,
            Applied = applied,
        };
}
