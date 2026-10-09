using Juvin.Core.Models;
using Juvin.Core.Providers;
using Juvin.Core.Rewrite;
using Juvin.Core.Tests.Fakes;

namespace Juvin.Core.Tests;

public sealed class RewriteSessionTests
{
    private static readonly RewriteContext s_context = new(
        "hi dana",
        [new ThreadMessage("Dana", [], null, "Can you confirm?")],
        "Re: timeline",
        ThreadCompleteness.EntireThread,
        0
    );

    private readonly FakeModelProvider _codex = new(ProcessingMode.Codex);
    private readonly FakeModelProvider _local = new(ProcessingMode.Local);

    [Fact]
    public async Task FollowUp_SendsOnlyTheInstructionAndKeepsTheHistory()
    {
        var session = Session(new AppSettings { LocalModel = "gemma4:26b" });
        _local.Reply = request => "reply " + request.Messages.Count;

        var first = await session.RunAsync(RewriteAction.Soften, null, null, CancellationToken.None);
        var second = await session.RunAsync(RewriteAction.Custom, "Make it warmer", null, CancellationToken.None);

        Assert.Equal("reply 1", first.Text);
        Assert.Equal("reply 3", second.Text);
        var followUp = _local.Requests[1];
        Assert.Equal(_local.Requests[0].SystemPrompt, followUp.SystemPrompt);
        Assert.Equal("gemma4:26b", followUp.Model);
        Assert.True(followUp.DisableThinking);
        Assert.Equal([ChatRole.User, ChatRole.Assistant, ChatRole.User], followUp.Messages.Select(m => m.Role));
        Assert.Contains("<draft", followUp.Messages[0].Content);
        Assert.Equal("reply 1", followUp.Messages[1].Content);
        Assert.Equal("Instruction: Make it warmer", followUp.Messages[2].Content);
        Assert.Equal(4, session.History.Count);
    }

    [Fact]
    public async Task Run_ForwardsThePreview()
    {
        var session = Session(new AppSettings());
        var preview = new List<string>();

        await session.RunAsync(RewriteAction.Shorter, null, new InlineProgress<string>(preview.Add), CancellationToken.None);

        Assert.Equal(["rewr", "rewritten"], preview);
    }

    [Fact]
    public async Task Custom_RequiresAnInstruction()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Session(new AppSettings()).RunAsync(RewriteAction.Custom, " ", null, CancellationToken.None)
        );
    }

    [Fact]
    public async Task EmptyReply_IsAFailureAndLeavesHistoryUntouched()
    {
        var session = Session(new AppSettings());
        _local.Reply = _ => "  \n";

        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            session.RunAsync(RewriteAction.Clearer, null, null, CancellationToken.None)
        );

        Assert.Equal(ProviderFailureKind.Protocol, ex.Kind);
        Assert.Empty(session.History);
    }

    [Fact]
    public async Task Provenance_WithoutCaptureCarriesNoPromptOrResponse()
    {
        var result = await Session(new AppSettings { LocalModel = "m" })
            .RunAsync(RewriteAction.Clearer, null, null, CancellationToken.None);

        var provenance = result.Provenance;
        Assert.Null(provenance.SystemPromptSent);
        Assert.Null(provenance.MessagesSent);
        Assert.Null(provenance.ResponseReceived);
        Assert.Equal(ProcessingMode.Local, provenance.Mode);
        Assert.Equal("Fake Local", provenance.ProviderName);
        Assert.Equal("m", provenance.ModelId);
        Assert.True(provenance.RanLocally);
        Assert.Equal(ThreadCompleteness.EntireThread, provenance.Completeness);
        Assert.Equal(1, provenance.ThreadMessageCount);
        Assert.Equal(7, provenance.DraftLength);
        Assert.Equal(100, provenance.InputTokens);
        Assert.Equal(20, provenance.OutputTokens);
    }

    [Fact]
    public async Task Provenance_WithCaptureRecordsWhatWasSent()
    {
        var result = await Session(new AppSettings { ProvenanceCaptureEnabled = true })
            .RunAsync(RewriteAction.Clearer, null, null, CancellationToken.None);

        Assert.Equal(RewritePromptBuilder.SystemPrompt, result.Provenance.SystemPromptSent);
        Assert.Contains("<draft", Assert.Single(result.Provenance.MessagesSent!).Content);
        Assert.Equal("rewritten", result.Provenance.ResponseReceived);
    }

    [Fact]
    public async Task Provenance_OfAFollowUpRecordsTheWholeConversationSent()
    {
        var session = Session(new AppSettings { ProvenanceCaptureEnabled = true });
        await session.RunAsync(RewriteAction.Soften, null, null, CancellationToken.None);

        var result = await session.RunAsync(RewriteAction.Custom, "Warmer", null, CancellationToken.None);

        Assert.Equal(
            [ChatRole.User, ChatRole.Assistant, ChatRole.User],
            result.Provenance.MessagesSent!.Select(m => m.Role)
        );
    }

    [Fact]
    public async Task Failure_KeepsThePartialResponseAndLeavesHistoryUntouched()
    {
        var session = Session(new AppSettings { ProvenanceCaptureEnabled = true });
        _local.FailAfterPartial = new ProviderException(ProviderFailureKind.Unreachable, "lost");

        await Assert.ThrowsAsync<ProviderException>(() =>
            session.RunAsync(RewriteAction.Clearer, null, null, CancellationToken.None)
        );

        Assert.Equal("rewr", session.LastProvenance?.ResponseReceived);
        Assert.Empty(session.History);
    }

    [Fact]
    public async Task Truncation_MarksTheCompleteness()
    {
        var context = s_context with
        {
            Thread = [.. Enumerable.Repeat(new ThreadMessage("x", [], null, new string('y', 500)), 3)],
        };
        var session = new RewriteSession(Selector(), new AppSettings(), context, new RewritePromptBuilder(threadBudget: 600));

        var result = await session.RunAsync(RewriteAction.Clearer, null, null, CancellationToken.None);

        Assert.Equal(ThreadCompleteness.TruncatedForModelLimit, result.Provenance.Completeness);
        Assert.Equal(1, result.Provenance.ThreadMessageCount);
    }

    [Fact]
    public async Task Session_UsesOnlyTheConfiguredModeEvenWhenItFails()
    {
        var session = Session(new AppSettings { ProcessingMode = ProcessingMode.Local });
        _local.FailAfterPartial = new ProviderException(ProviderFailureKind.Unreachable, "down");

        await Assert.ThrowsAsync<ProviderException>(() =>
            session.RunAsync(RewriteAction.Clearer, null, null, CancellationToken.None)
        );

        Assert.Single(_local.Requests);
        Assert.Empty(_codex.Requests);
    }

    [Theory]
    [InlineData(ProcessingMode.Local)]
    [InlineData(ProcessingMode.Codex)]
    public void Selector_ReturnsTheProviderForTheRequestedMode(ProcessingMode mode)
    {
        var provider = Selector().Select(new AppSettings { ProcessingMode = mode });

        Assert.Equal(mode, provider.Mode);
    }

    [Fact]
    public void Selector_RejectsAFactoryThatReturnsAnotherMode()
    {
        var selector = new ProviderSelector(_ => _codex, _ => _codex);

        Assert.Throws<InvalidOperationException>(() => selector.Select(new AppSettings()));
    }

    [Fact]
    public void Session_SelectsTheProviderOnce()
    {
        var calls = 0;
        var selector = new ProviderSelector(
            _ =>
            {
                calls++;
                return _local;
            },
            _ => _codex
        );

        var session = new RewriteSession(selector, new AppSettings(), s_context);

        Assert.Same(_local, session.Provider);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void DefaultSelector_BuildsTheRealProviders()
    {
        var selector = ProviderSelector.CreateDefault(new HttpClient(), new FakeProcessRunner(_ => new ProcessResult(0, "", "")));

        Assert.IsType<OpenAiCompatibleProvider>(selector.Select(new AppSettings()));
        Assert.IsType<CodexCliProvider>(selector.Select(new AppSettings { ProcessingMode = ProcessingMode.Codex }));
    }

    private ProviderSelector Selector() => new(_ => _local, _ => _codex);

    private RewriteSession Session(AppSettings settings) => new(Selector(), settings, s_context);
}
