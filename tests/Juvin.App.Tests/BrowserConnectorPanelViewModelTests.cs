using System.Collections.Concurrent;
using Juvin.App.Services;
using Juvin.App.Tests.Fakes;
using Juvin.App.ViewModels;
using Juvin.Core.Connector;
using Juvin.Core.Localization;
using Juvin.Platform;

namespace Juvin.App.Tests;

public sealed class BrowserConnectorPanelViewModelTests : IAsyncDisposable
{
    private const string Hello =
        """{"v":1,"type":"hello","extension":{"id":"extension@juvin.app","version":"0.1.0"},"browser":{"name":"firefox","version":"157.0"}}""";

    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(5);
    private static readonly BrowserTarget s_chrome = new("chrome", "Google Chrome", BrowserFamily.Chromium, BrowserPackaging.Native);
    private static readonly BrowserTarget s_firefox = new("firefox-flatpak", "Firefox", BrowserFamily.Firefox, BrowserPackaging.Flatpak);

    private readonly ConnectorService _connector;
    private readonly BrowserConnectorPanelViewModel _panel;
    private readonly ConcurrentQueue<Action> _posted = new();
    private readonly FakeNativeHostRegistrar _registrar = new();
    private readonly FakeConnectorSocket _socket = new();

    public BrowserConnectorPanelViewModelTests()
    {
        var localizer = new Localizer();
        _connector = new ConnectorService(_socket, _registrar, new FakeSettingsService(), localizer);
        _panel = new BrowserConnectorPanelViewModel(_registrar, _connector, localizer, _posted.Enqueue);
    }

    public async ValueTask DisposeAsync()
    {
        _panel.Dispose();
        await _connector.StopAsync();
    }

    [Fact]
    public void Detect_ListsBrowsersWithTheirRegistrationState()
    {
        _registrar.Browsers.AddRange([s_chrome, s_firefox]);
        _registrar.States[s_chrome.Id] = HostRegistration.Stale;

        _panel.DetectCommand.Execute(null);

        Assert.True(_panel.HasBrowsers);
        Assert.Equal(["Google Chrome", "Firefox"], _panel.Browsers.Select(row => row.DisplayName));
        var chrome = _panel.Browsers[0];
        Assert.Equal("Update", chrome.RegisterLabel);
        Assert.True(chrome.NeedsAttention);
        Assert.False(chrome.HasPackaging);
        var firefox = _panel.Browsers[1];
        Assert.Equal("Not registered", firefox.StateText);
        Assert.Equal("Flatpak", firefox.PackagingLabel);
        Assert.True(firefox.CanRegister);
    }

    [Fact]
    public void Detect_WithNoBrowsers_ShowsTheEmptyState()
    {
        _panel.DetectCommand.Execute(null);

        Assert.False(_panel.HasBrowsers);
    }

    [Fact]
    public async Task Register_Success_MarksTheRowRegisteredAndBindsNewSandboxEndpoints()
    {
        _registrar.Browsers.Add(s_firefox);
        _registrar.EndpointAddedOnRegister = "/run/user/1000/app/org.mozilla.firefox/juvin/connector.sock";
        _panel.DetectCommand.Execute(null);
        var row = _panel.Browsers[0];

        await _panel.RegisterCommand.ExecuteAsync(row);

        Assert.Equal(HostRegistration.Registered, row.State);
        Assert.Equal("Registered", row.StateText);
        Assert.False(row.CanRegister);
        Assert.False(row.HasError);
        Assert.Equal([_registrar.EndpointAddedOnRegister], _socket.AddedAddresses);
    }

    [Fact]
    public async Task Register_Success_ShowsAnUnboundSandboxSocketOnTheRow()
    {
        _registrar.Browsers.Add(s_firefox);
        _registrar.EndpointAddedOnRegister = "/run/user/1000/app/org.mozilla.firefox/juvin/connector.sock";
        _socket.AddAddressFailure = new IOException("in use");
        _panel.DetectCommand.Execute(null);
        var row = _panel.Browsers[0];

        await _panel.RegisterCommand.ExecuteAsync(row);

        Assert.Equal(HostRegistration.Registered, row.State);
        Assert.True(row.HasError);
        Assert.Contains(_registrar.EndpointAddedOnRegister, row.Error);
    }

    [Fact]
    public async Task Detect_ShowsAnUnboundSandboxSocketOnTheBrowserThatOwnsIt()
    {
        _registrar.Browsers.AddRange([s_chrome, s_firefox]);
        _registrar.States[s_firefox.Id] = HostRegistration.Registered;
        _registrar.Endpoints.Add(new SandboxEndpoint(s_firefox.Id, "/run/user/1000/app/org.mozilla.firefox/juvin/connector.sock"));
        _socket.AddAddressFailure = new IOException("in use");

        await _panel.DetectCommand.ExecuteAsync(null);

        Assert.False(_panel.Browsers[0].HasError);
        Assert.True(_panel.Browsers[1].HasError);
    }

    [Fact]
    public async Task Detect_KeepsARowWhoseRegistrationIsStillRunning()
    {
        _registrar.Browsers.Add(s_chrome);
        _registrar.RegisterGate = new TaskCompletionSource();
        await _panel.DetectCommand.ExecuteAsync(null);
        var row = _panel.Browsers[0];
        var registration = _panel.RegisterCommand.ExecuteAsync(row);

        await _panel.DetectCommand.ExecuteAsync(null);
        Assert.Same(row, _panel.Browsers[0]);

        _registrar.RegisterGate.SetResult();
        await registration;

        Assert.Equal(HostRegistration.Registered, _panel.Browsers[0].State);
    }

    [Fact]
    public async Task Register_Failure_ShowsTheMessageOnTheRow()
    {
        _registrar.Browsers.Add(s_chrome);
        _registrar.RegisterFailure = "Chrome's configuration folder is read-only.";
        _panel.DetectCommand.Execute(null);
        var row = _panel.Browsers[0];

        await _panel.RegisterCommand.ExecuteAsync(row);

        Assert.Equal(HostRegistration.NotRegistered, row.State);
        Assert.Equal(_registrar.RegisterFailure, row.Error);
        Assert.False(row.IsBusy);
        Assert.True(row.CanRegister);
    }

    [Fact]
    public async Task Test_WithNoExtensionConnected_SaysSo()
    {
        await _panel.TestCommand.ExecuteAsync(null);

        Assert.Equal("No extension connected", _panel.TestResult);
        Assert.False(_panel.HasClients);
    }

    [Fact]
    public async Task AConnectingExtension_IsListedThroughThePostedRefresh()
    {
        await _connector.StartAsync(CancellationToken.None);
        await using var stream = await _socket.ConnectAsync(s_timeout);
        Assert.NotNull(stream);

        await NativeMessagingFraming.WriteFrameAsync(stream, Hello, CancellationToken.None);
        await WaitUntilAsync(() => !_posted.IsEmpty);
        Assert.False(_panel.HasClients);

        while (_posted.TryDequeue(out var action))
        {
            action();
        }

        Assert.True(_panel.HasClients);
        Assert.Equal(["Firefox 157.0, extension 0.1.0"], _panel.ConnectedClients);
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
