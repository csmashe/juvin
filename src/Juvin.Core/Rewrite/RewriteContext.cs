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
    VisibleTextOnly,
    DraftOnly,
    TruncatedForModelLimit,
}

/// <param name="Thread">Messages oldest first.</param>
/// <param name="CollapsedCount">Messages the page showed as collapsed and that were not captured.</param>
/// <param name="IsMarkdown">The draft is in the connector's Markdown subset, and the rewrite may use it too.</param>
public sealed record RewriteContext(
    string Draft,
    IReadOnlyList<ThreadMessage> Thread,
    string? Subject,
    ThreadCompleteness Completeness,
    int CollapsedCount,
    bool IsMarkdown = false
);
