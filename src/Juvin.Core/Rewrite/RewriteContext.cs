namespace Juvin.Core.Rewrite;

public sealed record ThreadMessage(
    string From,
    IReadOnlyList<string> To,
    DateTimeOffset? Sent,
    string Body
);

public enum ThreadCompleteness
{
    EntireThread,
    LoadedMessagesOnly,
    // ReSharper disable once UnusedMember.Global -- reported by the generic page adapter in Phase 3
    VisibleTextOnly,
    DraftOnly,
    TruncatedForModelLimit,
}

/// <param name="Thread">Messages oldest first.</param>
/// <param name="CollapsedCount">Messages the page showed as collapsed and that were not captured.</param>
public sealed record RewriteContext(
    string Draft,
    IReadOnlyList<ThreadMessage> Thread,
    string? Subject,
    ThreadCompleteness Completeness,
    int CollapsedCount
);
