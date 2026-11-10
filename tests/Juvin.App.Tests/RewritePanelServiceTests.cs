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
    private readonly FakeModelProvider _local = new(ProcessingMode.Local);
    private readonly RewritePanelViewModel _viewModel;

    public RewritePanelServiceTests()
    {
        _viewModel = new RewritePanelViewModel(
            new ProviderSelector(_ => _local, _ => new FakeModelProvider(ProcessingMode.Codex)),
            new FakeSettingsService(),
            new FakeHistoryService(),
            new Localizer(),
            action => action()
        );
    }

    [Fact]
    public void AnOlderCapturePostedAfterANewerOne_IsIgnored()
    {
        var posted = new List<Action>();
        var shows = 0;
        var service = new RewritePanelService(_viewModel, () => shows++, () => { }, posted.Add);

        service.Show(new FakeRewriteTarget(), 2, Capture("newer"));
        service.Show(new FakeRewriteTarget(), 1, Capture("older"));
        foreach (var post in posted)
        {
            post();
        }

        Assert.Equal("newer", _viewModel.Subject);
        Assert.Equal(1, shows);
    }

    [Fact]
    public void Show_LoadsTheCaptureIdle()
    {
        var service = new RewritePanelService(_viewModel, () => { }, () => { }, action => action());

        service.Show(new FakeRewriteTarget(), 1, Capture("idle"));

        Assert.True(_viewModel.IsIdle);
        Assert.Empty(_local.Requests);
    }

    [Fact]
    public void Stop_CancelsTheRunningRewriteAndHidesThePanel()
    {
        _local.CompletionGate = new TaskCompletionSource();
        var hides = 0;
        var service = new RewritePanelService(_viewModel, () => { }, () => hides++, action => action());
        service.Start();
        service.Show(new FakeRewriteTarget(), 1, Capture("running"));
        _ = _viewModel.SelectActionCommand.ExecuteAsync(_viewModel.Actions[0]);
        Assert.True(_viewModel.IsRunning);

        service.Stop();

        Assert.False(_viewModel.IsRunning);
        Assert.Equal(1, hides);
    }

    [Fact]
    public void ADeclinedCaptureWithNoTarget_ShowsTheErrorWithRecaptureDisabled()
    {
        var localizer = new Localizer();
        var service = new RewritePanelService(_viewModel, () => { }, () => { }, action => action());

        service.Show(null, 1, new ContextResult(null, new ConnectorError(ConnectorService.NoBrowserCode, "No browser.")));

        Assert.True(_viewModel.HasError);
        Assert.Equal(localizer["Panel.Error.NoBrowser"], _viewModel.ErrorMessage);
        Assert.False(_viewModel.CanRecapture);
    }

    [Fact]
    public async Task TheHotkey_IsAimedAtTheShownCapturesBrowserOnlyWhileThePanelHasFocus()
    {
        await using var session = new ConnectorSession(new StreamConnectorChannel(new MemoryStream()), () => throw new InvalidOperationException());
        var aims = new List<ConnectorSession?>();
        var service = new RewritePanelService(_viewModel, () => { }, () => { }, action => action(), aims.Add);
        service.Start();

        service.Show(new ConnectorRewriteTarget(session), 1, Declined("no-selection"));
        service.SetActive(true);
        service.SetActive(false);
        service.SetActive(true);
        _viewModel.CloseCommand.Execute(null);

        Assert.Equal([null, session, null, session, null], aims);
    }

    [Fact]
    public async Task TheHotkey_IsAimedAgainWhenThePanelIsShownAgainStillReportingFocus()
    {
        await using var session = new ConnectorSession(new StreamConnectorChannel(new MemoryStream()), () => throw new InvalidOperationException());
        ConnectorSession? aim = null;
        var service = new RewritePanelService(_viewModel, () => { }, () => { }, action => action(), aimed => aim = aimed);
        service.Start();
        service.Show(new ConnectorRewriteTarget(session), 1, Declined("no-selection"));
        service.SetActive(true);
        _viewModel.CloseCommand.Execute(null);

        service.Show(new ConnectorRewriteTarget(session), 2, Declined("no-selection"));

        Assert.Same(session, aim);
    }

    [Fact]
    public async Task TheHotkey_IsNotAimedWhenThePanelIsShownAgainAfterLosingFocus()
    {
        await using var session = new ConnectorSession(new StreamConnectorChannel(new MemoryStream()), () => throw new InvalidOperationException());
        var aims = new List<ConnectorSession?>();
        var service = new RewritePanelService(_viewModel, () => { }, () => { }, action => action(), aims.Add);
        service.Start();
        service.Show(new ConnectorRewriteTarget(session), 1, Declined("no-selection"));
        service.SetActive(true);
        _viewModel.CloseCommand.Execute(null);
        service.SetActive(false);

        service.Show(new ConnectorRewriteTarget(session), 2, Declined("no-selection"));
        var aimedOnShow = aims[^1];
        service.SetActive(true);

        Assert.Null(aimedOnShow);
        Assert.Same(session, aims[^1]);
    }

    [Fact]
    public async Task TheHotkey_IsNotAimedAtABrowserForACaptureNoBrowserAnswered()
    {
        await using var session = new ConnectorSession(new StreamConnectorChannel(new MemoryStream()), () => throw new InvalidOperationException());
        ConnectorSession? aim = null;
        var service = new RewritePanelService(_viewModel, () => { }, () => { }, action => action(), aimed => aim = aimed);
        service.Show(new ConnectorRewriteTarget(session), 1, Declined("no-selection"));
        service.SetActive(true);

        service.Show(null, 2, Declined(ConnectorService.BrowserNotFocusedCode));

        Assert.Null(aim);
    }

    [Theory]
    [InlineData("browser-not-focused", true)]
    [InlineData("timeout", false)]
    [InlineData("disconnected", false)]
    [InlineData("no-selection", true)]
    public void AnsweredByBrowser_OnlyABrowsersOwnAnswerCounts(string code, bool expected) =>
        Assert.Equal(expected, ConnectorService.AnsweredByBrowser(new ContextResult(null, new ConnectorError(code, "x"))));

    private static ContextResult Declined(string code) => new(null, new ConnectorError(code, "Declined."));

    private static ContextResult Capture(string subject)
    {
        var context = new RewriteContext("draft", [], subject, ThreadCompleteness.DraftOnly, 0);
        return new ContextResult(new CapturedContext(context, "test-page", "e-1", "sha256:abc", null, 0, 5), null);
    }
}
