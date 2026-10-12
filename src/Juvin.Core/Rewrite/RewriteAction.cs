namespace Juvin.Core.Rewrite;

/// <param name="TitleKey">String table key for a built-in action's name; empty for a user-defined action.</param>
/// <param name="Instruction">Sent as the last line of the prompt; empty for <see cref="Custom" />.</param>
/// <remarks>
///     <see cref="BuiltIn" /> holds the pristine prompts. Use <see cref="RewriteActionCatalog" /> for the
///     actions as the user configured them, and compare actions by <see cref="Id" />.
/// </remarks>
public sealed record RewriteAction(string Id, string TitleKey, string Instruction)
{
    /// <summary>The user's name for a user-defined action; null for built-ins, which use <see cref="TitleKey" />.</summary>
    public string? Title { get; init; }

    public static RewriteAction Soften { get; } = new(
        "soften",
        "Action.Soften",
        "Soften the tone so it reads warmer and more courteous, without weakening any request, deadline, or commitment."
    );

    public static RewriteAction Clearer { get; } = new(
        "clearer",
        "Action.Clearer",
        "Make the draft clearer and easier to follow: lead with the main point and tidy the structure."
    );

    public static RewriteAction Shorter { get; } = new(
        "shorter",
        "Action.Shorter",
        "Make the draft shorter. Cut filler and repetition but keep every fact and commitment."
    );

    // ReSharper disable once MemberCanBePrivate.Global -- part of the built-in action catalog like its siblings
    public static RewriteAction Grammar { get; } = new(
        "grammar",
        "Action.Grammar",
        "Fix spelling, grammar, punctuation, and capitalization only. Change nothing else."
    );

    public static RewriteAction Custom { get; } = new("custom", "Action.Custom", "");

    public static RewriteAction Missing { get; } = new(
        "missing",
        "Action.Missing",
        "Do not rewrite the draft. Compare it with the requests and questions in the thread and list, as short bullet points, each one the draft does not address. If it addresses all of them, say so in one sentence."
    );

    public static IReadOnlyList<RewriteAction> BuiltIn { get; } =
        [Soften, Clearer, Shorter, Grammar, Custom, Missing];

    public bool IsFreeText => Id == Custom.Id;

    public bool IsMissing => Id == Missing.Id;
}
