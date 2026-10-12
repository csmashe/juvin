using Juvin.App.Services;
using Juvin.App.Tests.Fakes;
using Juvin.App.ViewModels;
using Juvin.Core.Connector;
using Juvin.Core.Localization;
using Juvin.Core.Models;
using Juvin.Core.Providers;
using Juvin.Core.Rewrite;

namespace Juvin.App.Tests;

public sealed class RewritePanelServiceTests
{
    [Fact]
    public void AnOlderCapturePostedAfterANewerOne_IsIgnored()
    {
        var settings = new FakeSettingsService();
        var localizer = new Localizer();
        var local = new FakeModelProvider(ProcessingMode.Local);
        var viewModel = new RewritePanelViewModel(
            new ProviderSelector(_ => local, _ => new FakeModelProvider(ProcessingMode.Codex)),
            settings,
            new FakeHistoryService(),
            localizer,
            action => action()
        );
        var posted = new List<Action>();
        var shows = 0;
        var service = new RewritePanelService(
            new ConnectorService(new FakeConnectorSocket(), new FakeNativeHostRegistrar(), settings, localizer),
            viewModel,
            () => shows++,
            () => { },
            posted.Add
        );
        var session = new ConnectorSession(new StreamConnectorChannel(new MemoryStream()), () => throw new NotSupportedException());

        service.OnCaptureCompleted(session, 2, Capture("newer"));
        service.OnCaptureCompleted(session, 1, Capture("older"));
        foreach (var post in posted)
        {
            post();
        }

        Assert.Equal("newer", viewModel.Subject);
        Assert.Single(local.Requests);
        Assert.Equal(1, shows);
    }

    [Fact]
    public void Stop_CancelsTheRunningRewriteAndHidesThePanel()
    {
        var settings = new FakeSettingsService();
        var localizer = new Localizer();
        var local = new FakeModelProvider(ProcessingMode.Local) { CompletionGate = new TaskCompletionSource() };
        var viewModel = new RewritePanelViewModel(
            new ProviderSelector(_ => local, _ => new FakeModelProvider(ProcessingMode.Codex)),
            settings,
            new FakeHistoryService(),
            localizer,
            action => action()
        );
        var hides = 0;
        var service = new RewritePanelService(
            new ConnectorService(new FakeConnectorSocket(), new FakeNativeHostRegistrar(), settings, localizer),
            viewModel,
            () => { },
            () => hides++,
            action => action()
        );
        service.Start();
        var session = new ConnectorSession(new StreamConnectorChannel(new MemoryStream()), () => throw new NotSupportedException());
        service.OnCaptureCompleted(session, 1, Capture("running"));
        Assert.True(viewModel.IsRunning);

        service.Stop();

        Assert.False(viewModel.IsRunning);
        Assert.Equal(1, hides);
    }

    [Fact]
    public async Task AHotkeyWithNoBrowser_ShowsTheErrorWithRecaptureDisabled()
    {
        var settings = new FakeSettingsService();
        var localizer = new Localizer();
        var viewModel = new RewritePanelViewModel(
            new ProviderSelector(_ => new FakeModelProvider(ProcessingMode.Local), _ => new FakeModelProvider(ProcessingMode.Codex)),
            settings,
            new FakeHistoryService(),
            localizer,
            action => action()
        );
        var connector = new ConnectorService(new FakeConnectorSocket(), new FakeNativeHostRegistrar(), settings, localizer);
        var shows = 0;
        var service = new RewritePanelService(connector, viewModel, () => shows++, () => { }, action => action());
        service.Start();

        await connector.TriggerCaptureAsync();

        Assert.Equal(1, shows);
        Assert.True(viewModel.HasError);
        Assert.Equal(localizer["Panel.Error.NoBrowser"], viewModel.ErrorMessage);
        Assert.False(viewModel.CanRecapture);
    }

    [Theory]
    [InlineData("browser-not-focused", true)]
    [InlineData("timeout", false)]
    [InlineData("no-selection", true)]
    public void AnsweredByBrowser_OnlyABrowsersOwnAnswerCounts(string code, bool expected) =>
        Assert.Equal(expected, ConnectorService.AnsweredByBrowser(new ContextResult(null, new ConnectorError(code, "x"))));

    [Fact]
    public void ANotFocusedBrowser_ShowsTheErrorWithRecaptureDisabled() => AssertRecaptureDisabled(ConnectorService.BrowserNotFocusedCode);

    [Fact]
    public void ATimedOutBrowser_ShowsTheErrorWithRecaptureDisabled() => AssertRecaptureDisabled(ConnectorRewriteTarget.TimeoutCode);

    private static void AssertRecaptureDisabled(string code)
    {
        var settings = new FakeSettingsService();
        var localizer = new Localizer();
        var viewModel = new RewritePanelViewModel(
            new ProviderSelector(_ => new FakeModelProvider(ProcessingMode.Local), _ => new FakeModelProvider(ProcessingMode.Codex)),
            settings,
            new FakeHistoryService(),
            localizer,
            action => action()
        );
        var connector = new ConnectorService(new FakeConnectorSocket(), new FakeNativeHostRegistrar(), settings, localizer);
        var service = new RewritePanelService(connector, viewModel, () => { }, () => { }, action => action());
        service.Start();
        var session = new ConnectorSession(new StreamConnectorChannel(new MemoryStream()), () => throw new NotSupportedException());

        service.OnCaptureCompleted(session, 1, new ContextResult(null, new ConnectorError(code, "Not from a focused browser.")));

        Assert.True(viewModel.HasError);
        Assert.False(viewModel.CanRecapture);
    }

    private static ContextResult Capture(string subject)
    {
        var context = new RewriteContext("draft", [], subject, ThreadCompleteness.DraftOnly, 0);
        return new ContextResult(new CapturedContext(context, "test-page", "e-1", "sha256:abc", null, 0, 5), null);
    }
}
