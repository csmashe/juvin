// ReSharper disable UnusedAutoPropertyAccessor.Global -- read by the JSON round-trip and the export
using Juvin.Core.Providers;
using Juvin.Core.Rewrite;

namespace Juvin.Core.Models;

/// <summary>One completed rewrite kept in history. Holds email content, so it is never traced.</summary>
public sealed record HistoryEntry
{
    public string Id { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public string ActionId { get; init; } = "";
    public string ActionTitle { get; init; } = "";
    public string? Subject { get; init; }
    public string Adapter { get; init; } = "";
    public string Draft { get; init; } = "";
    public string Result { get; init; } = "";
    public ProcessingMode Mode { get; init; }
    public string ProviderName { get; init; } = "";
    public string ModelId { get; init; } = "";
    public bool RanLocally { get; init; }
    public int ThreadMessageCount { get; init; }
    public ThreadCompleteness Completeness { get; init; }
    public TimeSpan Duration { get; init; }

    /// <summary>The prompt sent for this run; set only when provenance capture is turned on.</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>Every turn sent with the call, oldest first; set only when provenance capture is turned on.</summary>
    public IReadOnlyList<ChatMessage>? MessagesSent { get; init; }

    /// <summary>Set once Apply replaced the draft in the editor.</summary>
    public bool Applied { get; init; }

    public static string NewId() => Guid.NewGuid().ToString("N");
}
