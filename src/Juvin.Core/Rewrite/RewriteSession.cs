using System.Diagnostics;
using Juvin.Core.Models;
using Juvin.Core.Providers;

namespace Juvin.Core.Rewrite;

/// <summary>
///     One rewrite conversation over a captured draft. The thread and draft go out once, in the
///     first turn; follow-ups add just their instruction and keep the earlier turns as history.
///     The provider is chosen once, when the session is created, and never changes.
/// </summary>
public sealed class RewriteSession
{
    private readonly RewritePromptBuilder _builder;
    private readonly RewriteContext _context;
    private readonly List<ChatMessage> _history = [];
    private readonly AppSettings _settings;

    private ThreadCompleteness _completeness;
    private int _includedMessages;
    private string _systemPrompt = "";

    /// <exception cref="ProviderException">The provider for the processing mode is misconfigured.</exception>
    public RewriteSession(
        ProviderSelector selector,
        AppSettings settings,
        RewriteContext context,
        RewritePromptBuilder? builder = null
    )
    {
        Provider = selector.Select(settings);
        _settings = settings;
        _context = context;
        _builder = builder ?? new RewritePromptBuilder();
        CaptureProvenance = settings.ProvenanceCaptureEnabled;
    }

    public IModelProvider Provider { get; }

    /// <summary>Whether the next run records the prompt and response; the panel refreshes it from the settings before each run.</summary>
    public bool CaptureProvenance { get; set; }

    public IReadOnlyList<ChatMessage> History => _history;

    /// <summary>The provenance of the last run, including one that failed.</summary>
    public RewriteProvenance? LastProvenance { get; private set; }

    public async Task<RewriteResult> RunAsync(
        RewriteAction action,
        string? customInstruction,
        IProgress<string>? preview,
        CancellationToken ct
    )
    {
        var instruction = action.IsCustom ? customInstruction : action.Instruction;
        if (string.IsNullOrWhiteSpace(instruction))
        {
            throw new ArgumentException("A custom action needs an instruction.", nameof(customInstruction));
        }

        string userPrompt;
        if (_history.Count == 0)
        {
            var built = _builder.Build(_context, instruction, _settings.SenderName);
            _systemPrompt = built.SystemPrompt;
            userPrompt = built.UserPrompt;
            _completeness = built.Truncated ? ThreadCompleteness.TruncatedForModelLimit : _context.Completeness;
            _includedMessages = built.IncludedMessageCount;
        }
        else
        {
            userPrompt = RewritePromptBuilder.FollowUp(instruction);
        }

        var userTurn = new ChatMessage(ChatRole.User, userPrompt);
        var request = new ChatRequest
        {
            SystemPrompt = _systemPrompt,
            Messages = [.. _history, userTurn],
            Model = NullIfBlank(_settings.ProcessingMode == ProcessingMode.Local ? _settings.LocalModel : _settings.CodexModel),
            DisableThinking = _settings.LocalThinkingDisabled,
        };

        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var latest = "";
        var progress = new InlineProgress<string>(text =>
        {
            latest = text;
            preview?.Report(PlaceholderFiller.FillStreaming(text, _settings.SenderName));
        });

        try
        {
            var result = await Provider.CompleteAsync(request, progress, ct);
            if (string.IsNullOrWhiteSpace(result.Text))
            {
                throw new ProviderException(ProviderFailureKind.Protocol, "The model returned an empty reply.");
            }

            _history.Add(userTurn);
            _history.Add(new ChatMessage(ChatRole.Assistant, result.Text));
            LastProvenance = CreateProvenance(request, result.Model, result.Text, startedAt, stopwatch.Elapsed) with
            {
                InputTokens = result.InputTokens,
                OutputTokens = result.OutputTokens,
            };
            return new RewriteResult(PlaceholderFiller.Fill(result.Text, _settings.SenderName), LastProvenance);
        }
        catch
        {
            LastProvenance = CreateProvenance(request, request.Model ?? "default", latest, startedAt, stopwatch.Elapsed);
            throw;
        }
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private RewriteProvenance CreateProvenance(
        ChatRequest request,
        string model,
        string response,
        DateTimeOffset startedAt,
        TimeSpan duration
    )
    {
        var capture = CaptureProvenance;
        return new RewriteProvenance
        {
            Mode = Provider.Mode,
            ProviderName = Provider.DisplayName,
            ModelId = model,
            RanLocally = Provider.RunsLocally,
            SystemPromptSent = capture ? request.SystemPrompt : null,
            MessagesSent = capture ? request.Messages : null,
            ResponseReceived = capture ? response : null,
            Completeness = _completeness,
            ThreadMessageCount = _includedMessages,
            DraftLength = _context.Draft.Length,
            StartedAt = startedAt,
            Duration = duration,
        };
    }
}
