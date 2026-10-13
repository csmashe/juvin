using System.Text.Json;
using Juvin.App.Services;
using Juvin.App.Tests.Fakes;
using Juvin.App.ViewModels;
using Juvin.Core.Connector;
using Juvin.Core.Localization;
using Juvin.Core.Models;
using Juvin.Core.Providers;
using Juvin.Core.Rewrite;

namespace Juvin.App.Tests;

public sealed class ActionMenuServiceTests : IAsyncDisposable
{
    private const string Hello =
        """{"v":1,"type":"hello","extension":{"id":"extension@juvin.app","version":"0.1.0"},"browser":{"name":"firefox","version":"157.0"}}""";

    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(5);

    private readonly FakeConnectorChannel _channel = new();
    private readonly FakeModelProvider _codex = new(ProcessingMode.Codex);
    private readonly FakeHistoryService _history = new();
    private readonly FakeModelProvider _local = new(ProcessingMode.Local);
    private readonly Localizer _localizer = new();
    private readonly Task _loop;
    private readonly RewritePanelViewModel _panel;
    private readonly ActionMenuService _service;
    private readonly ConnectorSession _session;
    private readonly FakeSettingsService _settings = new();
    private readonly Lock _ui = new();
    private int _panelShows;

    public ActionMenuServiceTests()
    {
        var connector = new ConnectorService(new FakeConnectorSocket(), new FakeNativeHostRegistrar(), _settings, _localizer);
        var providers = new ProviderSelector(_ => _local, _ => _codex);
        _panel = new RewritePanelViewModel(providers, _settings, _history, _localizer, Post);
        var panelService = new RewritePanelService(_panel, () => Interlocked.Increment(ref _panelShows), () => { }, Post);
        _service = new ActionMenuService(connector, panelService, providers, _settings, _history, _localizer, Post);
        _session = new ConnectorSession(_channel, () => connector.BuildConfig(_settings.Current));
        _session.MenuChosen += _service.OnMenuChosen;
        _session.MenuClosed += _service.OnMenuClosed;
        _loop = _session.RunAsync(CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        _local.CompletionGate?.TrySetResult();
        await _session.DisposeAsync();
        await _loop.WaitAsync(s_timeout);
    }

    [Fact]
    public async Task ACapture_SendsTheMenuWithTheFiveSlotsTheCustomActionsAndTheStatus()
    {
        _settings.Update(s => s with
        {
            CustomRewriteActions = [new CustomRewriteAction("custom-1", "Formal", "Make it formal.")],
            RewriteInstructionOverrides = new Dictionary<string, string> { ["soften"] = "Be gentle." },
        });
        await SayHelloAsync();

        _service.OnCaptureCompleted(_session, 1, Capture());
        var show = await NextAsync("menu.show");

        Assert.Equal("menu-1", show.GetProperty("menuId").GetString());
        Assert.Equal("e-1", show.GetProperty("editorId").GetString());
        Assert.Equal(
            ["missing", "soften", "clearer", "shorter", "grammar"],
            show.GetProperty("slots").EnumerateArray().Select(slot => slot.GetProperty("id").GetString())
        );
        Assert.Equal("What am I missing?", show.GetProperty("slots")[0].GetProperty("title").GetString());
        var more = Assert.Single(show.GetProperty("more").EnumerateArray());
        Assert.Equal(("custom-1", "Formal"), (more.GetProperty("id").GetString(), more.GetProperty("title").GetString()));
        Assert.Equal(
            ["2 messages", "Entire thread", _localizer.Format("Panel.Provider.Local", _localizer["Panel.Provider.DefaultModel"])],
            show.GetProperty("status").EnumerateArray().Select(line => line.GetString())
        );
        Assert.Equal(0, _panelShows);
    }

    [Fact]
    public async Task AServerOnAnotherMachine_IsLabelledPrivateServerBeforeAnyRun()
    {
        _settings.Update(s => s with { LocalServerUrl = "http://10.0.3.20:11434", LocalModel = "qwen3.5:27b" });
        await SayHelloAsync();

        _service.OnCaptureCompleted(_session, 1, Capture());
        var show = await NextAsync("menu.show");

        Assert.Equal(
            _localizer.Format("Panel.Provider.PrivateServer", "qwen3.5:27b"),
            show.GetProperty("status")[2].GetString()
        );
    }

    [Theory]
    [InlineData("""{"v":1,"type":"menu.response","id":"ID","ok":false,"error":{"code":"unsupported","message":"No menu."}}""")]
    [InlineData("""{"v":1,"type":"error","id":"ID","error":{"code":"unknown-type","message":"Unknown message type."}}""")]
    public async Task AMenuThatCannotBeShown_OpensThePanelIdle(string reply)
    {
        await SayHelloAsync();
        _service.OnCaptureCompleted(_session, 1, Capture());
        var show = await NextAsync("menu.show");

        _channel.Deliver(reply.Replace("ID", show.GetProperty("id").GetString()));

        await WaitUntilAsync(() => Volatile.Read(ref _panelShows) == 1);
        Assert.True(_panel.IsIdle);
        Assert.Equal("Re: timeline", _panel.Subject);
        Assert.True(_panel.CanRecapture);
        Assert.Empty(_local.Requests);
    }

    [Fact]
    public async Task ADeclinedCapture_GoesToThePanel()
    {
        await SayHelloAsync();

        _service.OnCaptureCompleted(_session, 1, new ContextResult(null, new ConnectorError("no-selection", "Nothing is selected.")));

        Assert.Equal(1, _panelShows);
        Assert.Equal(_localizer["Panel.Error.NoSelection"], _panel.ErrorMessage);
        Assert.True(_panel.CanRecapture);
        Assert.False(_channel.HasSent);
    }

    [Fact]
    public async Task AnOlderCapturePostedAfterANewerOne_IsIgnored()
    {
        await SayHelloAsync();
        _service.OnCaptureCompleted(_session, 2, Capture());
        await ShowAsync();

        _service.OnCaptureCompleted(_session, 1, Capture(editorId: "e-old"));

        Assert.False(_channel.HasSent);
        Assert.Equal(0, _panelShows);
    }

    [Fact]
    public async Task ChoosingAnAction_RunsItAppliesItAndMarksTheHistoryEntryApplied()
    {
        _settings.Update(s => s with { HistoryEnabled = true });
        var menuId = await OpenMenuAsync();

        Choose(menuId, "soften");

        var running = await NextAsync("menu.update");
        Assert.Equal(("running", "soften"), (running.GetProperty("state").GetString(), running.GetProperty("action").GetString()));
        var apply = await NextAsync("apply.request");
        Assert.Equal("reply 1", apply.GetProperty("text").GetString());
        Assert.Equal("sha256:abc", apply.GetProperty("expectedHash").GetString());
        Assert.Contains(RewriteAction.Soften.Instruction, _local.Requests[0].Messages[^1].Content);
        Reply(apply, """{"ok":true}""", "apply.response");

        var applied = await NextAsync("menu.update");
        Assert.Equal("applied", applied.GetProperty("state").GetString());
        Assert.Equal(menuId, applied.GetProperty("menuId").GetString());
        await WaitUntilAsync(() => _history.Entries is [{ Applied: true, ActionId: "soften" }]);
        Assert.Empty(_codex.Requests);
    }

    [Fact]
    public async Task WhatAmIMissing_SendsTheAnswerAndDoesNotApply()
    {
        var menuId = await OpenMenuAsync();

        Choose(menuId, "missing");

        await NextAsync("menu.update");
        var answer = await NextAsync("menu.update");
        Assert.Equal(("answer", "reply 1"), (answer.GetProperty("state").GetString(), answer.GetProperty("text").GetString()));

        // The menu stays open, so the user can pick again.
        Choose(menuId, "shorter");
        Assert.Equal("running", (await NextAsync("menu.update")).GetProperty("state").GetString());
    }

    [Fact]
    public async Task AskingAfterWhatAmIMissing_ContinuesTheAdviceAndDoesNotApply()
    {
        var menuId = await OpenMenuAsync();
        Choose(menuId, "missing");
        await NextAsync("menu.update");
        await NextAsync("menu.update");

        _channel.Deliver($$"""{"v":1,"type":"menu.choose","menuId":"{{menuId}}","action":"custom","instruction":"Which one matters most?"}""");

        await NextAsync("menu.update");
        var answer = await NextAsync("menu.update");
        Assert.Equal("answer", answer.GetProperty("state").GetString());
        Assert.Equal(2, _local.Requests.Count);
        Assert.True(_local.Requests[1].Messages.Count > _local.Requests[0].Messages.Count);
        Assert.False(_channel.HasSent);
    }

    [Fact]
    public async Task ANewCapture_DismissesWhatThePanelWasWorkingOn()
    {
        _local.CompletionGate = new TaskCompletionSource();
        await SayHelloAsync();
        _service.OnCaptureCompleted(_session, 1, Capture());
        var show = await NextAsync("menu.show");
        Reply(show, """{"ok":false,"error":{"code":"unsupported","message":"No menu."}}""", "menu.response");
        await WaitUntilAsync(() => Volatile.Read(ref _panelShows) == 1);
        _ = _panel.SelectActionCommand.ExecuteAsync(_panel.Actions[0]);
        Assert.True(_panel.IsRunning);

        _service.OnCaptureCompleted(_session, 2, Capture());
        await ShowAsync();

        Assert.False(_panel.IsRunning);
    }

    [Fact]
    public async Task ADraftChangedApply_SendsTheResult()
    {
        var menuId = await OpenMenuAsync();
        Choose(menuId, "clearer");
        await NextAsync("menu.update");
        var apply = await NextAsync("apply.request");

        Reply(apply, """{"ok":false,"error":{"code":"draft-changed","message":"The draft changed."}}""", "apply.response");

        var update = await NextAsync("menu.update");
        Assert.Equal(("draft-changed", "reply 1"), (update.GetProperty("state").GetString(), update.GetProperty("text").GetString()));
    }

    [Fact]
    public async Task AnApplyTheEditorRefuses_SendsTheErrorWithTheResultForCopy()
    {
        var menuId = await OpenMenuAsync();
        Choose(menuId, "clearer");
        await NextAsync("menu.update");
        var apply = await NextAsync("apply.request");

        Reply(apply, """{"ok":false,"error":{"code":"editor-gone","message":"Gone."}}""", "apply.response");

        var update = await NextAsync("menu.update");
        Assert.Equal("error", update.GetProperty("state").GetString());
        Assert.Equal(_localizer["Panel.Error.EditorGone"], update.GetProperty("text").GetString());
        Assert.False(update.GetProperty("canRetry").GetBoolean());
        Assert.Equal("reply 1", update.GetProperty("result").GetString());
    }

    [Fact]
    public async Task AProviderFailure_SendsARetryableErrorAndNeverFallsBack()
    {
        _local.Failure = new ProviderException(ProviderFailureKind.Unreachable, "The local model server did not respond in time.");
        var menuId = await OpenMenuAsync();

        Choose(menuId, "clearer");

        await NextAsync("menu.update");
        var error = await NextAsync("menu.update");
        Assert.Equal("error", error.GetProperty("state").GetString());
        Assert.Equal("The local model server did not respond in time.", error.GetProperty("text").GetString());
        Assert.True(error.GetProperty("canRetry").GetBoolean());
        Assert.Empty(_codex.Requests);
    }

    [Fact]
    public async Task AnUnknownAction_SendsAnErrorWithoutRetry()
    {
        var menuId = await OpenMenuAsync();

        Choose(menuId, "gone");

        var error = await NextAsync("menu.update");
        Assert.Equal(_localizer["Menu.Error.UnknownAction"], error.GetProperty("text").GetString());
        Assert.False(error.GetProperty("canRetry").GetBoolean());
        Assert.Empty(_local.Requests);
    }

    [Fact]
    public async Task Review_OpensThePanelIdleWithoutClosingTheMenu()
    {
        var menuId = await OpenMenuAsync();

        Choose(menuId, "review");

        await WaitUntilAsync(() => Volatile.Read(ref _panelShows) == 1);
        Assert.True(_panel.IsIdle);
        await PingAsync();
        Choose(menuId, "soften");
        await PingAsync();
        Assert.Empty(_local.Requests);
    }

    [Fact]
    public async Task ACloseFromTheExtension_CancelsTheRun()
    {
        _local.CompletionGate = new TaskCompletionSource();
        var menuId = await OpenMenuAsync();
        Choose(menuId, "soften");
        await NextAsync("menu.update");

        _channel.Deliver($$"""{"v":1,"type":"menu.close","menuId":"{{menuId}}"}""");
        await PingAsync();
        _local.CompletionGate.SetResult();
        Choose(menuId, "shorter");

        await PingAsync();
        Assert.Single(_local.Requests);
    }

    [Fact]
    public async Task ANewCapture_ClosesTheOldMenuAndCancelsItsRun()
    {
        _local.CompletionGate = new TaskCompletionSource();
        var first = await OpenMenuAsync();
        Choose(first, "soften");
        await NextAsync("menu.update");

        _service.OnCaptureCompleted(_session, 2, Capture(editorId: "e-2"));

        Assert.Equal(first, (await NextAsync("menu.close")).GetProperty("menuId").GetString());
        var show = await NextAsync("menu.show");
        Assert.Equal(("menu-2", "e-2"), (show.GetProperty("menuId").GetString(), show.GetProperty("editorId").GetString()));
        _local.CompletionGate.SetResult();
        await PingAsync();
    }

    [Fact]
    public async Task AChoiceWhileRunning_IsIgnored()
    {
        _local.CompletionGate = new TaskCompletionSource();
        var menuId = await OpenMenuAsync();
        Choose(menuId, "soften");
        await NextAsync("menu.update");

        Choose(menuId, "shorter");
        await PingAsync();
        _local.CompletionGate.SetResult();

        var apply = await NextAsync("apply.request");
        Assert.Single(_local.Requests);
        Reply(apply, """{"ok":true}""", "apply.response");
        Assert.Equal("applied", (await NextAsync("menu.update")).GetProperty("state").GetString());
    }

    [Fact]
    public async Task Recapture_ClosesTheMenuAndShowsANewOneOverTheFreshCapture()
    {
        var menuId = await OpenMenuAsync();

        Choose(menuId, "recapture");

        var request = await NextAsync("context.request");
        Assert.Equal("e-1", request.GetProperty("editorId").GetString());
        _channel.Deliver(CaptureJson(request.GetProperty("id").GetString()!, "e-1"));
        Assert.Equal(menuId, (await NextAsync("menu.close")).GetProperty("menuId").GetString());
        Assert.Equal("menu-2", (await NextAsync("menu.show")).GetProperty("menuId").GetString());
    }

    [Fact]
    public async Task ADeclinedRecapture_SendsAnErrorWithoutRetry()
    {
        var menuId = await OpenMenuAsync();

        Choose(menuId, "recapture");

        var request = await NextAsync("context.request");
        Reply(request, """{"ok":false,"error":{"code":"editor-gone","message":"Gone."}}""", "context.response");
        var error = await NextAsync("menu.update");
        Assert.Equal(_localizer["Panel.Error.EditorGone"], error.GetProperty("text").GetString());
        Assert.False(error.GetProperty("canRetry").GetBoolean());
    }

    private void Post(Action action)
    {
        lock (_ui)
        {
            action();
        }
    }

    private async Task SayHelloAsync()
    {
        _channel.Deliver(Hello);
        await NextAsync("config");
    }

    /// <summary>Shows a menu over a capture and answers it ok; returns its id.</summary>
    private async Task<string> OpenMenuAsync()
    {
        await SayHelloAsync();
        _service.OnCaptureCompleted(_session, 1, Capture());
        return await ShowAsync();
    }

    private async Task<string> ShowAsync()
    {
        var show = await NextAsync("menu.show");
        Reply(show, """{"ok":true}""", "menu.response");
        return show.GetProperty("menuId").GetString()!;
    }

    private void Choose(string menuId, string action) =>
        _channel.Deliver($$"""{"v":1,"type":"menu.choose","menuId":"{{menuId}}","action":"{{action}}"}""");

    /// <summary>Answers a request with <paramref name="body" />'s members added to the envelope.</summary>
    private void Reply(JsonElement request, string body, string type) =>
        _channel.Deliver($$"""{"v":1,"type":"{{type}}","id":"{{request.GetProperty("id").GetString()}}",{{body[1..]}}""");

    /// <summary>Waits for everything delivered so far to be handled, and everything it sent to be read.</summary>
    private async Task PingAsync()
    {
        _channel.Deliver("""{"v":1,"type":"ping","id":"sync"}""");
        await NextAsync("pong");
    }

    private async Task<JsonElement> NextAsync(string type)
    {
        var message = await _channel.NextSentAsync();
        Assert.Equal(type, message.GetProperty("type").GetString());
        return message;
    }

    private static ContextResult Capture(string editorId = "e-1")
    {
        var thread = new List<ThreadMessage>
        {
            new("Dana <dana@example.com>", [], null, "Can you send the report by Friday?"),
            new("Me <me@example.com>", [], null, "Looking into it."),
        };
        var context = new RewriteContext("hi dana", thread, "Re: timeline", ThreadCompleteness.EntireThread, 0);
        return new ContextResult(new CapturedContext(context, "gmail", editorId, "sha256:abc", null, 0, 7), null);
    }

    private static string CaptureJson(string id, string editorId) =>
        $$"""
        {"v":1,"type":"context.response","id":"{{id}}","ok":true,
         "capture":{"adapter":"gmail","editor":{"id":"{{editorId}}","hash":"sha256:def"},
           "selection":{"text":"hi dana","start":0,"end":7},
           "thread":{"completeness":"draft-only","messages":[]}
         }
        }
        """;

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
