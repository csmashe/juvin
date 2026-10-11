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

    private static ContextResult Capture(string subject)
    {
        var context = new RewriteContext("draft", [], subject, ThreadCompleteness.DraftOnly, 0);
        return new ContextResult(new CapturedContext(context, "test-page", "e-1", "sha256:abc", null, 0, 5), null);
    }
}
