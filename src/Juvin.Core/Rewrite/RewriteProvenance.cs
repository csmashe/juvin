// ReSharper disable UnusedAutoPropertyAccessor.Global -- serialized by the CLI and shown by the Context panel
using Juvin.Core.Models;
using Juvin.Core.Providers;

namespace Juvin.Core.Rewrite;

/// <summary>
///     What one rewrite call sent and to whom. The prompt and response strings are filled only
///     when provenance capture is turned on; everything else is always recorded.
/// </summary>
public sealed record RewriteProvenance
{
    public required ProcessingMode Mode { get; init; }

    public required string ProviderName { get; init; }

    public required string ModelId { get; init; }

    public required bool RanLocally { get; init; }

    public string? SystemPromptSent { get; init; }

    /// <summary>Every turn sent with the call, oldest first; the last one is this call's instruction.</summary>
    public IReadOnlyList<ChatMessage>? MessagesSent { get; init; }

    /// <summary>Set when the call finishes; partial when it failed mid-way.</summary>
    public string? ResponseReceived { get; init; }

    public required ThreadCompleteness Completeness { get; init; }

    public required int ThreadMessageCount { get; init; }

    public required int DraftLength { get; init; }

    public int? InputTokens { get; init; }

    public int? OutputTokens { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required TimeSpan Duration { get; init; }
}

public sealed record RewriteResult(string Text, RewriteProvenance Provenance);
