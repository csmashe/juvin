using Juvin.App.Tests.Fakes;
using Juvin.App.ViewModels;
using Juvin.Core.Connector;
using Juvin.Core.Localization;
using Juvin.Core.Models;
using Juvin.Core.Providers;
using Juvin.Core.Rewrite;

namespace Juvin.App.Tests;

public sealed class RewritePanelViewModelTests
{
    private const string Draft = "hi dana, I can send it friday";

    private readonly FakeModelProvider _codex = new(ProcessingMode.Codex) { SupportsStreaming = false };
    private readonly Localizer _localizer = new();
    private readonly FakeHistoryService _history = new();
    private readonly FakeSettingsService _settings = new();
    private readonly FakeRewriteTarget _target = new();
    private FakeModelProvider _local = new(ProcessingMode.Local);

    [Fact]
    public async Task ACapture_RunsTheDefaultActionAndStreamsIntoThePreview()
    {
        _settings.Update(s => s with { DefaultRewriteAction = "soften" });
        _local.CompletionGate = new TaskCompletionSource();
        var vm = Create();

        var run = vm.LoadAsync(_target, Capture());

        Assert.True(vm.IsRunning);
        Assert.Equal("rep", vm.ResultText);
        Assert.Equal("soften", vm.Actions.Single(chip => chip.IsSelected).Action.Id);
        Assert.Contains(RewriteAction.Soften.Instruction, _local.Requests[0].Messages[^1].Content);
        Assert.False(vm.CanApply);

        _local.CompletionGate.SetResult();
        await run;

        Assert.Equal(RewritePanelState.Done, vm.State);
        Assert.Equal("reply 1", vm.ResultText);
        Assert.True(vm.CanApply);
        Assert.True(vm.CanCopy);
    }

    [Fact]
    public async Task AnUnknownDefaultAction_FallsBackToClearer()
    {
        _settings.Update(s => s with { DefaultRewriteAction = "no-such-action" });
        var vm = Create();

        await vm.LoadAsync(_target, Capture());

        Assert.Contains(RewriteAction.Clearer.Instruction, _local.Requests[0].Messages[^1].Content);
        Assert.Equal("clearer", vm.Actions.Single(chip => chip.IsSelected).Action.Id);
    }

    [Fact]
    public void TheChips_AreTheBuiltInActionsWithoutCustom()
    {
        var vm = Create();

        Assert.Equal(
            ["Soften", "Make clearer", "Make shorter", "Fix grammar", "What am I missing?"],
            vm.Actions.Select(chip => chip.Title)
        );
    }

    [Fact]
    public async Task PickingAChip_StartsANewSession()
    {
        var vm = Create();
        await vm.LoadAsync(_target, Capture());

        await vm.SelectActionCommand.ExecuteAsync(vm.Actions.Single(chip => chip.Action == RewriteAction.Shorter));

        var second = _local.Requests[1];
        Assert.Single(second.Messages);
        Assert.Contains(RewriteAction.Shorter.Instruction, second.Messages[0].Content);
        Assert.Equal("reply 1", vm.ResultText);
        Assert.Equal("shorter", vm.Actions.Single(chip => chip.IsSelected).Action.Id);
    }

    [Fact]
    public async Task AFollowUp_KeepsTheEarlierTurns()
    {
        var vm = Create();
        await vm.LoadAsync(_target, Capture());

        vm.FollowUpText = "  keep the deadline but sound less annoyed ";
        await vm.SendFollowUpCommand.ExecuteAsync(null);

        var followUp = _local.Requests[1];
        Assert.Equal([ChatRole.User, ChatRole.Assistant, ChatRole.User], followUp.Messages.Select(m => m.Role));
        Assert.Equal("reply 1", followUp.Messages[1].Content);
        Assert.Equal("Instruction: keep the deadline but sound less annoyed", followUp.Messages[2].Content);
        Assert.Equal("reply 3", vm.ResultText);
        Assert.Equal("", vm.FollowUpText);
        Assert.True(vm.CanApply);
    }

    [Fact]
    public async Task AnEditedWhatAmIMissing_StillCannotBeApplied()
    {
        _settings.Update(s => s with { RewriteInstructionOverrides = new Dictionary<string, string> { ["missing"] = "List what is unanswered." } });
        var vm = Create();
        await vm.LoadAsync(_target, Capture());

        await vm.SelectActionCommand.ExecuteAsync(vm.Actions.Single(chip => chip.Action.Id == "missing"));

        Assert.Contains("List what is unanswered.", _local.Requests[^1].Messages[^1].Content);
        Assert.False(vm.CanApply);
    }

    [Fact]
    public async Task AnEditedInstruction_ReachesTheModel()
    {
        _settings.Update(s => s with
        {
            DefaultRewriteAction = "soften",
            RewriteInstructionOverrides = new Dictionary<string, string> { ["soften"] = "Be extra gentle." },
        });
        var vm = Create();

        await vm.LoadAsync(_target, Capture());

        var prompt = _local.Requests[0].Messages[^1].Content;
        Assert.Contains("Be extra gentle.", prompt);
        Assert.DoesNotContain(RewriteAction.Soften.Instruction, prompt);
        Assert.Equal("soften", vm.Actions.Single(chip => chip.IsSelected).Action.Id);
    }

    [Fact]
    public async Task AUserAction_IsAChipAndCanBeTheDefault()
    {
        _settings.Update(s => s with
        {
            CustomRewriteActions = [new CustomRewriteAction("custom-1", "Formal", "Make it formal.")],
            DefaultRewriteAction = "custom-1",
        });
        var vm = Create();

        await vm.LoadAsync(_target, Capture());

        Assert.Equal("Formal", vm.Actions[^1].Title);
        Assert.True(vm.Actions[^1].IsSelected);
        Assert.Contains("Make it formal.", _local.Requests[0].Messages[^1].Content);
        Assert.True(vm.CanApply);
    }

    [Fact]
    public async Task TheChips_FollowSettingsChangesAndKeepTheSelection()
    {
        _settings.Update(s => s with { CustomRewriteActions = [new CustomRewriteAction("custom-1", "Formal", "Make it formal.")] });
        var vm = Create();
        await vm.LoadAsync(_target, Capture());
        await vm.SelectActionCommand.ExecuteAsync(vm.Actions.Single(chip => chip.Action.Id == "custom-1"));

        _settings.Update(s => s with { CustomRewriteActions = [new CustomRewriteAction("custom-1", "Very formal", "Make it very formal.")] });

        var selected = vm.Actions.Single(chip => chip.IsSelected);
        Assert.Equal("Very formal", selected.Title);

        await vm.SelectActionCommand.ExecuteAsync(selected);
        Assert.Contains("Make it very formal.", _local.Requests[^1].Messages[^1].Content);

        _settings.Update(s => s with { CustomRewriteActions = [] });

        Assert.DoesNotContain(vm.Actions, chip => chip.Action.Id == "custom-1");
        Assert.DoesNotContain(vm.Actions, chip => chip.IsSelected);
        Assert.Equal(RewritePanelState.Done, vm.State);
    }

    [Fact]
    public async Task ASettingsChange_DoesNotInterruptARunningRewrite()
    {
        _local.CompletionGate = new TaskCompletionSource();
        var vm = Create();
        var run = vm.LoadAsync(_target, Capture());

        _settings.Update(s => s with { RewriteInstructionOverrides = new Dictionary<string, string> { ["clearer"] = "Changed." } });
        _local.CompletionGate.SetResult();
        await run;

        Assert.Equal(RewritePanelState.Done, vm.State);
        Assert.Equal("reply 1", vm.ResultText);
        Assert.Equal("clearer", vm.Actions.Single(chip => chip.IsSelected).Action.Id);
    }

    [Fact]
    public async Task WhatAmIMissing_CannotBeApplied()
    {
        var vm = Create();
        await vm.LoadAsync(_target, Capture());

        await vm.SelectActionCommand.ExecuteAsync(vm.Actions.Single(chip => chip.Action == RewriteAction.Missing));

        Assert.Equal(RewritePanelState.Done, vm.State);
        Assert.False(vm.CanApply);
        Assert.False(vm.ApplyCommand.CanExecute(null));
        Assert.False(vm.CanShowChanges);
        Assert.True(vm.CanCopy);
    }

    [Fact]
    public async Task ASuccessfulApply_SendsTheResultAndHides()
    {
        var vm = Create();
        var hides = 0;
        vm.HideRequested += () => hides++;
        await vm.LoadAsync(_target, Capture());

        await vm.ApplyCommand.ExecuteAsync(null);

        var (capture, text) = Assert.Single(_target.Applied);
        Assert.Equal("e-1", capture.EditorId);
        Assert.Equal("reply 1", text);
        Assert.Equal(1, hides);
    }

    [Fact]
    public async Task ADraftChangedApply_OffersCopyAndRecapture()
    {
        _target.ApplyError = new ConnectorError("draft-changed", "The draft changed since it was captured.");
        var vm = Create();
        var hides = 0;
        vm.HideRequested += () => hides++;
        await vm.LoadAsync(_target, Capture());

        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.True(vm.IsDraftChanged);
        Assert.Equal(_localizer["Panel.DraftChanged"], vm.Notice);
        Assert.False(vm.CanApply);
        Assert.True(vm.CopyCommand.CanExecute(null));
        Assert.True(vm.RecaptureCommand.CanExecute(null));
        Assert.Equal("reply 1", vm.ResultText);
        Assert.Equal(0, hides);
    }

    [Fact]
    public async Task ADraftChangedCapture_StaysStaleAfterANewAction()
    {
        _target.ApplyError = new ConnectorError("draft-changed", "The draft changed since it was captured.");
        var vm = Create();
        await vm.LoadAsync(_target, Capture());
        await vm.ApplyCommand.ExecuteAsync(null);

        await vm.SelectActionCommand.ExecuteAsync(vm.Actions.Single(chip => chip.Action == RewriteAction.Shorter));

        Assert.True(vm.IsDraftChanged);
        Assert.Equal(_localizer["Panel.DraftChanged"], vm.Notice);
        Assert.False(vm.CanApply);
        Assert.True(vm.CopyCommand.CanExecute(null));
    }

    [Fact]
    public async Task AnApplyAfterTheBrowserDisconnected_SaysSo()
    {
        _target.ApplyError = new ConnectorError(ConnectorErrorCodes.Disconnected, "The connector connection closed.");
        var vm = Create();
        await vm.LoadAsync(_target, Capture());

        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.False(vm.IsDraftChanged);
        Assert.Equal(_localizer["Panel.Error.Disconnected"], vm.Notice);
    }

    [Fact]
    public async Task AProviderFailure_ShowsTheMessageAndIsNotRetried()
    {
        _local.Failure = new ProviderException(ProviderFailureKind.Unreachable, "The local model server did not respond in time.");
        var vm = Create();

        await vm.LoadAsync(_target, Capture());

        Assert.True(vm.HasError);
        Assert.Equal("The local model server did not respond in time.", vm.ErrorMessage);
        Assert.True(vm.CanRetry);
        Assert.False(vm.CanApply);
        Assert.Single(_local.Requests);
        Assert.Empty(_codex.Requests);

        _local.Failure = null;
        await vm.RetryCommand.ExecuteAsync(null);

        Assert.Equal(2, _local.Requests.Count);
        Assert.Equal("reply 1", vm.ResultText);
    }

    [Fact]
    public async Task RetryAfterAFailedFirstTurn_UsesTheCurrentSettings()
    {
        var broken = new FakeModelProvider(ProcessingMode.Local)
        {
            Failure = new ProviderException(ProviderFailureKind.Configuration, "The model was not found."),
        };
        var vm = new RewritePanelViewModel(
            new ProviderSelector(s => s.LocalModel == "fixed" ? _local : broken, _ => _codex),
            _settings,
            _history,
            _localizer,
            action => action()
        );
        await vm.LoadAsync(_target, Capture());
        Assert.True(vm.HasError);

        _settings.Update(s => s with { LocalModel = "fixed" });
        await vm.RetryCommand.ExecuteAsync(null);

        Assert.Equal(RewritePanelState.Done, vm.State);
        Assert.Single(broken.Requests);
        Assert.Single(_local.Requests);
        Assert.Equal("reply 1", vm.ResultText);
    }

    [Fact]
    public async Task RetryAfterAFailedFollowUp_KeepsTheEarlierTurns()
    {
        var vm = Create();
        await vm.LoadAsync(_target, Capture());
        _local.Failure = new ProviderException(ProviderFailureKind.Unreachable, "The local model server did not respond in time.");
        vm.FollowUpText = "shorter please";
        await vm.SendFollowUpCommand.ExecuteAsync(null);
        Assert.True(vm.HasError);

        _local.Failure = null;
        await vm.RetryCommand.ExecuteAsync(null);

        var retried = _local.Requests[^1];
        Assert.Equal(3, _local.Requests.Count);
        Assert.Equal([ChatRole.User, ChatRole.Assistant, ChatRole.User], retried.Messages.Select(m => m.Role));
        Assert.Equal("reply 1", retried.Messages[1].Content);
        Assert.Equal("Instruction: shorter please", retried.Messages[2].Content);
        Assert.Equal(RewritePanelState.Done, vm.State);
    }

    [Fact]
    public async Task RetryAfterAFailedFollowUpOnAFailedFirstTurn_RunsTheFollowUpOnAFreshSession()
    {
        _local.Failure = new ProviderException(ProviderFailureKind.Unreachable, "The local model server did not respond in time.");
        var vm = Create();
        await vm.LoadAsync(_target, Capture());
        vm.FollowUpText = "shorter please";
        await vm.SendFollowUpCommand.ExecuteAsync(null);
        Assert.True(vm.HasError);

        _local.Failure = null;
        await vm.RetryCommand.ExecuteAsync(null);

        var retried = _local.Requests[^1];
        Assert.Equal(3, _local.Requests.Count);
        Assert.Single(retried.Messages);
        Assert.Contains("shorter please", retried.Messages[0].Content);
        Assert.Equal(RewritePanelState.Done, vm.State);
        Assert.Equal("clearer", vm.Actions.Single(chip => chip.IsSelected).Action.Id);
        Assert.True(vm.CanApply);
    }

    [Fact]
    public async Task Cancel_StopsTheRunAndOffersRetry()
    {
        _local.CompletionGate = new TaskCompletionSource();
        var vm = Create();
        var run = vm.LoadAsync(_target, Capture());

        vm.CancelCommand.Execute(null);
        await run;

        Assert.Equal(RewritePanelState.Failed, vm.State);
        Assert.Equal(_localizer["Panel.Cancelled"], vm.ErrorMessage);
        Assert.True(vm.CanRetry);
    }

    [Fact]
    public async Task Close_CancelsTheRunAndHides()
    {
        _local.CompletionGate = new TaskCompletionSource();
        var vm = Create();
        var hides = 0;
        vm.HideRequested += () => hides++;
        var run = vm.LoadAsync(_target, Capture());

        vm.CloseCommand.Execute(null);
        await run;

        Assert.Equal(1, hides);
        Assert.False(vm.IsRunning);
    }

    [Fact]
    public async Task ANewCapture_ReplacesTheOneRunning()
    {
        _local.CompletionGate = new TaskCompletionSource();
        var vm = Create();
        var first = vm.LoadAsync(_target, Capture(adapter: "generic"));
        var second = vm.LoadAsync(new FakeRewriteTarget(), Capture(adapter: "gmail"));

        _local.CompletionGate.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(RewritePanelState.Done, vm.State);
        Assert.Equal("Gmail", vm.AdapterLabel);
        Assert.Equal(2, _local.Requests.Count);
    }

    [Fact]
    public async Task ACaptureError_ShowsItsMessageAndAllowsRecapture()
    {
        var vm = Create();

        await vm.LoadAsync(_target, new ContextResult(null, new ConnectorError("no-selection", "Nothing is selected in the editor.")));

        Assert.True(vm.HasError);
        Assert.Equal(_localizer["Panel.Error.NoSelection"], vm.ErrorMessage);
        Assert.False(vm.CanRetry);
        Assert.False(vm.HasCapture);
        Assert.True(vm.RecaptureCommand.CanExecute(null));
        Assert.Empty(_local.Requests);
    }

    [Fact]
    public async Task Recapture_ReplacesTheCaptureAndRerunsTheCurrentAction()
    {
        var vm = Create();
        await vm.LoadAsync(_target, Capture());
        await vm.SelectActionCommand.ExecuteAsync(vm.Actions.Single(chip => chip.Action == RewriteAction.Grammar));
        _target.NextCapture = Capture(adapter: "test-page");

        await vm.RecaptureCommand.ExecuteAsync(null);

        Assert.Equal(["e-1"], _target.RecaptureEditorIds);
        Assert.Equal("Test page", vm.AdapterLabel);
        var rerun = _local.Requests[^1];
        Assert.Single(rerun.Messages);
        Assert.Contains(RewriteAction.Grammar.Instruction, rerun.Messages[0].Content);
        Assert.Equal(RewritePanelState.Done, vm.State);
    }

    [Fact]
    public async Task Recapture_IsAvailableAgainWhileTheNewRewriteRuns()
    {
        var vm = Create();
        await vm.LoadAsync(_target, Capture());
        _local.CompletionGate = new TaskCompletionSource();
        _target.NextCapture = Capture(editorId: "e-2");

        await vm.RecaptureCommand.ExecuteAsync(null);

        Assert.True(vm.IsRunning);
        Assert.True(vm.RecaptureCommand.CanExecute(null));

        _local.CompletionGate.SetResult();
    }

    [Fact]
    public async Task Recapture_AsksForTheEditorOfTheLatestCapture()
    {
        var vm = Create();
        await vm.LoadAsync(_target, Capture(editorId: "e-1"));
        _target.NextCapture = Capture(editorId: "e-2");
        await vm.RecaptureCommand.ExecuteAsync(null);

        _target.NextCapture = new ContextResult(null, new ConnectorError("editor-gone", "The editor is no longer open."));
        await vm.RecaptureCommand.ExecuteAsync(null);
        await vm.RecaptureCommand.ExecuteAsync(null);

        Assert.Equal(["e-1", "e-2", "e-2"], _target.RecaptureEditorIds);
        Assert.Equal(_localizer["Panel.Error.EditorGone"], vm.Notice);
        Assert.Equal(2, _local.Requests.Count);
    }

    [Fact]
    public async Task AFailedRecapture_KeepsTheResultAndShowsANotice()
    {
        var vm = Create();
        await vm.LoadAsync(_target, Capture());
        _target.NextCapture = new ContextResult(null, new ConnectorError("editor-gone", "The editor is no longer open."));

        await vm.RecaptureCommand.ExecuteAsync(null);

        Assert.Equal(RewritePanelState.Done, vm.State);
        Assert.Equal("reply 1", vm.ResultText);
        Assert.True(vm.CanCopy);
        Assert.True(vm.HasCapture);
        Assert.Equal(_localizer["Panel.Error.EditorGone"], vm.Notice);

        vm.FollowUpText = "shorter please";
        Assert.True(vm.SendFollowUpCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public async Task TheContextList_ShowsOnlyTheMessagesSentToTheModel(int included, bool expectHint)
    {
        var vm = Create(BuilderKeeping(included));

        await vm.LoadAsync(_target, Capture());

        Assert.Equal(included, vm.ContextMessages.Count);
        Assert.Equal("Me <me@example.com>", vm.ContextMessages[^1].From);
        Assert.Equal(expectHint, vm.HasOlderMessagesHint);
        Assert.Equal(expectHint ? _localizer["Panel.OlderMessagesLeftOutOne"] : null, vm.OlderMessagesHint);
    }

    [Fact]
    public async Task TheContextList_ShowsEveryCapturedMessageBeforeTheResult()
    {
        _local.CompletionGate = new TaskCompletionSource();
        var vm = Create(BuilderKeeping(1));

        var run = vm.LoadAsync(_target, Capture());

        Assert.Equal(2, vm.ContextMessages.Count);
        Assert.False(vm.HasOlderMessagesHint);

        _local.CompletionGate.SetResult();
        await run;

        Assert.Single(vm.ContextMessages);
    }

    [Fact]
    public async Task ARecaptureAnsweredAfterClose_IsCancelledAndDiscarded()
    {
        var vm = Create();
        await vm.LoadAsync(_target, Capture());
        _target.RecaptureGate = new TaskCompletionSource<ContextResult>();

        var recapture = vm.RecaptureCommand.ExecuteAsync(null);
        vm.CloseCommand.Execute(null);
        _target.RecaptureGate.SetResult(Capture(editorId: "e-2"));
        await recapture;

        Assert.True(_target.LastRecaptureToken.IsCancellationRequested);
        Assert.Single(_local.Requests);
        Assert.Equal("reply 1", vm.ResultText);
        Assert.False(vm.IsBusy);
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.Equal("e-1", Assert.Single(_target.Applied).Capture.EditorId);
    }

    [Fact]
    public async Task AFollowUpAfterWhatAmIMissing_CannotBeApplied()
    {
        var vm = Create();
        await vm.LoadAsync(_target, Capture());
        await vm.SelectActionCommand.ExecuteAsync(vm.Actions.Single(chip => chip.Action == RewriteAction.Missing));

        vm.FollowUpText = "what about the budget?";
        await vm.SendFollowUpCommand.ExecuteAsync(null);

        Assert.Equal(RewritePanelState.Done, vm.State);
        Assert.False(vm.CanApply);
        Assert.False(vm.CanShowChanges);
    }

    [Fact]
    public async Task AFollowUpAfterClearer_CanBeApplied()
    {
        var vm = Create();
        await vm.LoadAsync(_target, Capture());
        await vm.SelectActionCommand.ExecuteAsync(vm.Actions.Single(chip => chip.Action == RewriteAction.Clearer));

        vm.FollowUpText = "shorter please";
        await vm.SendFollowUpCommand.ExecuteAsync(null);

        Assert.Equal(RewritePanelState.Done, vm.State);
        Assert.True(vm.CanApply);
        Assert.True(vm.CanShowChanges);
    }

    [Fact]
    public async Task ANonStreamingProvider_ShowsTheWorkingLineInsteadOfAPreview()
    {
        _local = new FakeModelProvider(ProcessingMode.Local) { SupportsStreaming = false, CompletionGate = new TaskCompletionSource() };
        var vm = Create();

        var run = vm.LoadAsync(_target, Capture());

        Assert.True(vm.IsWorking);
        Assert.Equal("Working with Fake Local...", vm.WorkingText);
        Assert.Equal("", vm.ResultText);

        _local.CompletionGate!.SetResult();
        await run;

        Assert.False(vm.IsWorking);
        Assert.Equal("reply 1", vm.ResultText);
    }

    [Theory]
    [InlineData(ThreadCompleteness.EntireThread, 0, "Entire thread")]
    [InlineData(ThreadCompleteness.LoadedMessagesOnly, 0, "Loaded messages only")]
    [InlineData(ThreadCompleteness.LoadedMessagesOnly, 3, "Loaded messages only, +3 collapsed")]
    [InlineData(ThreadCompleteness.VisibleTextOnly, 0, "Visible text only")]
    [InlineData(ThreadCompleteness.DraftOnly, 0, "Draft only")]
    [InlineData(ThreadCompleteness.TruncatedForModelLimit, 0, "Truncated to fit")]
    public async Task TheContextChips_DescribeTheCapture(ThreadCompleteness completeness, int collapsed, string expected)
    {
        var vm = Create();

        await vm.LoadAsync(_target, Capture(completeness, collapsed));

        Assert.Equal(expected, vm.CompletenessLabel);
        Assert.Equal("Gmail", vm.AdapterLabel);
        Assert.Equal("2 messages", vm.MessageCountLabel);
        Assert.Equal("Local: fake-model", vm.ProviderLabel);
        Assert.Equal("Re: timeline", vm.Subject);
        Assert.Equal(["Dana <dana@example.com>", "Me <me@example.com>"], vm.ContextMessages.Select(m => m.From));
    }

    [Fact]
    public async Task CodexMode_IsLabelledAsCloud()
    {
        _settings.Update(s => s with { ProcessingMode = ProcessingMode.Codex });
        var vm = Create();

        await vm.LoadAsync(_target, Capture());

        Assert.Equal("Codex: cloud", vm.ProviderLabel);
        Assert.Single(_codex.Requests);
        Assert.Empty(_local.Requests);
    }

    [Fact]
    public async Task TheChangesView_DiffsTheDraftAgainstTheResult()
    {
        _local.Reply = _ => "hi Dana, I can send it Friday";
        var vm = Create();
        await vm.LoadAsync(_target, Capture());

        Assert.Empty(vm.Changes);
        vm.ShowChangesViewCommand.Execute(null);

        Assert.True(vm.IsChangesVisible);
        Assert.Equal(Draft, string.Concat(vm.Changes.Where(s => s.Kind != DiffKind.Added).Select(s => s.Text)));
        Assert.Contains(vm.Changes, s => s is { Kind: DiffKind.Added, Text: "Dana," });
    }

    [Fact]
    public async Task Copy_WritesTheResultAndShowsTheHint()
    {
        var copied = new List<string>();
        var vm = Create();
        vm.CopyToClipboard = text =>
        {
            copied.Add(text);
            return Task.CompletedTask;
        };
        await vm.LoadAsync(_target, Capture());

        await vm.CopyCommand.ExecuteAsync(null);

        Assert.Equal(["reply 1"], copied);
        Assert.True(vm.ShowCopied);
    }

    [Fact]
    public async Task WithHistoryOn_EachCompletedRunIsKept()
    {
        _settings.Update(s => s with { HistoryEnabled = true, HistoryRetentionDays = 7 });
        var vm = Create();

        await vm.LoadAsync(_target, Capture());
        vm.FollowUpText = "shorter please";
        await vm.SendFollowUpCommand.ExecuteAsync(null);

        Assert.Equal(2, _history.Entries.Count);
        Assert.Equal("reply 3", _history.Entries[0].Result);
        Assert.Equal("custom", _history.Entries[0].ActionId);
        var first = _history.Entries[1];
        Assert.Equal(32, first.Id.Length);
        Assert.Equal("clearer", first.ActionId);
        Assert.Equal("Make clearer", first.ActionTitle);
        Assert.Equal("Re: timeline", first.Subject);
        Assert.Equal("gmail", first.Adapter);
        Assert.Equal(Draft, first.Draft);
        Assert.Equal("reply 1", first.Result);
        Assert.Equal(ProcessingMode.Local, first.Mode);
        Assert.Equal("Fake Local", first.ProviderName);
        Assert.Equal("fake-model", first.ModelId);
        Assert.True(first.RanLocally);
        Assert.Equal(2, first.ThreadMessageCount);
        Assert.Equal(ThreadCompleteness.EntireThread, first.Completeness);
        Assert.False(first.Applied);
        Assert.Null(first.SystemPrompt);
        Assert.Null(first.MessagesSent);
        Assert.All(_history.Prunes, prune => Assert.Equal(7, prune.RetentionDays));
        Assert.Equal(2, _history.Prunes.Count);
    }

    [Fact]
    public async Task WithProvenanceCaptureOn_TheEntryKeepsThePrompts()
    {
        _settings.Update(s => s with { HistoryEnabled = true, ProvenanceCaptureEnabled = true });
        var vm = Create();

        await vm.LoadAsync(_target, Capture());
        vm.FollowUpText = "shorter please";
        await vm.SendFollowUpCommand.ExecuteAsync(null);

        var followUp = _history.Entries[0];
        Assert.Equal(_local.Requests[1].SystemPrompt, followUp.SystemPrompt);
        Assert.Equal(_local.Requests[1].Messages, followUp.MessagesSent);
        Assert.Equal(3, followUp.MessagesSent!.Count);
        Assert.Contains(Draft, followUp.MessagesSent[0].Content);
    }

    [Fact]
    public async Task ProvenanceCaptureTurnedOnMidSession_KeepsPromptsFromTheNextRun()
    {
        _settings.Update(s => s with { HistoryEnabled = true });
        var vm = Create();
        await vm.LoadAsync(_target, Capture());
        _settings.Update(s => s with { ProvenanceCaptureEnabled = true });

        vm.FollowUpText = "shorter please";
        await vm.SendFollowUpCommand.ExecuteAsync(null);

        Assert.Null(_history.Entries[1].MessagesSent);
        Assert.Equal(3, _history.Entries[0].MessagesSent!.Count);
    }

    [Fact]
    public async Task ProvenanceCaptureTurnedOffMidSession_StopsKeepingPrompts()
    {
        _settings.Update(s => s with { HistoryEnabled = true, ProvenanceCaptureEnabled = true });
        var vm = Create();
        await vm.LoadAsync(_target, Capture());
        _settings.Update(s => s with { ProvenanceCaptureEnabled = false });

        vm.FollowUpText = "shorter please";
        await vm.SendFollowUpCommand.ExecuteAsync(null);

        Assert.NotNull(_history.Entries[1].MessagesSent);
        Assert.Null(_history.Entries[0].SystemPrompt);
        Assert.Null(_history.Entries[0].MessagesSent);
    }

    [Fact]
    public async Task WithHistoryOff_NothingIsKept()
    {
        var vm = Create();

        await vm.LoadAsync(_target, Capture());
        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.Empty(_history.Entries);
        Assert.Empty(_history.Prunes);
    }

    [Fact]
    public async Task ASuccessfulApply_MarksTheEntryApplied()
    {
        _settings.Update(s => s with { HistoryEnabled = true });
        var vm = Create();
        await vm.LoadAsync(_target, Capture());
        await vm.SelectActionCommand.ExecuteAsync(vm.Actions.Single(chip => chip.Action == RewriteAction.Shorter));

        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.True(_history.Entries[0].Applied);
        Assert.False(_history.Entries[1].Applied);
    }

    [Fact]
    public async Task ARefusedApply_LeavesTheEntryUnapplied()
    {
        _settings.Update(s => s with { HistoryEnabled = true });
        _target.ApplyError = new ConnectorError("draft-changed", "The draft changed since it was captured.");
        var vm = Create();
        await vm.LoadAsync(_target, Capture());

        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.False(Assert.Single(_history.Entries).Applied);
    }

    [Fact]
    public async Task AFailedRun_KeepsNothing()
    {
        _settings.Update(s => s with { HistoryEnabled = true });
        _local.Failure = new ProviderException(ProviderFailureKind.Unreachable, "The local model server did not respond in time.");
        var vm = Create();

        await vm.LoadAsync(_target, Capture());

        Assert.True(vm.HasError);
        Assert.Empty(_history.Entries);
    }

    [Fact]
    public async Task AHistoryWriteFailure_StillShowsTheResult()
    {
        _settings.Update(s => s with { HistoryEnabled = true });
        _history.Failure = new IOException("disk full");
        var vm = Create();

        await vm.LoadAsync(_target, Capture());
        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(RewritePanelState.Done, vm.State);
        Assert.Equal("reply 1", vm.ResultText);
        Assert.Single(_target.Applied);
    }

    private RewritePanelViewModel Create(RewritePromptBuilder? builder = null) =>
        new(new ProviderSelector(_ => _local, _ => _codex), _settings, _history, _localizer, action => action(), builder);

    /// <summary>The smallest budget that keeps the newest <paramref name="count" /> messages of <see cref="Capture" />.</summary>
    private static RewritePromptBuilder BuilderKeeping(int count)
    {
        var context = Capture().Capture!.Context;
        for (var budget = 0; ; budget++)
        {
            var builder = new RewritePromptBuilder(budget);
            if (builder.Build(context, "x").IncludedMessageCount == count)
            {
                return builder;
            }
        }
    }

    private static ContextResult Capture(
        ThreadCompleteness completeness = ThreadCompleteness.EntireThread,
        int collapsed = 0,
        string adapter = "gmail",
        string editorId = "e-1"
    )
    {
        var thread = new List<ThreadMessage>
        {
            new("Dana <dana@example.com>", [], new DateTimeOffset(2026, 10, 8, 9, 12, 0, TimeSpan.Zero), "Can you send the report by Friday?"),
            new("Me <me@example.com>", [], null, "Looking into it."),
        };
        var context = new RewriteContext(Draft, thread, "Re: timeline", completeness, collapsed);
        return new ContextResult(new CapturedContext(context, adapter, editorId, "sha256:abc", null, 0, Draft.Length), null);
    }
}
