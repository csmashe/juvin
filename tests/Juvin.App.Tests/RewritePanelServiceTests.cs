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

    [Theory]
    [InlineData("browser-not-focused", true)]
    [InlineData("timeout", false)]
    [InlineData("no-selection", true)]
    public void AnsweredByBrowser_OnlyABrowsersOwnAnswerCounts(string code, bool expected) =>
        Assert.Equal(expected, ConnectorService.AnsweredByBrowser(new ContextResult(null, new ConnectorError(code, "x"))));

    private static ContextResult Capture(string subject)
    {
        var context = new RewriteContext("draft", [], subject, ThreadCompleteness.DraftOnly, 0);
        return new ContextResult(new CapturedContext(context, "test-page", "e-1", "sha256:abc", null, 0, 5), null);
    }
}
