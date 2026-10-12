using Juvin.Core.Models;

namespace Juvin.Core.Rewrite;

/// <summary>
///     The rewrite actions as configured in one settings snapshot: the built-ins with any edited prompt
///     applied, then the user's actions, then "What am I missing?".
/// </summary>
public sealed class RewriteActionCatalog
{
    public RewriteActionCatalog(AppSettings settings)
    {
        var prompts = settings.RewriteActionPrompts;
        var builtIn = RewriteAction.BuiltIn
            .Select(action =>
                !action.IsFreeText && prompts.TryGetValue(action.Id, out var prompt) && !string.IsNullOrWhiteSpace(prompt)
                    ? action with { Instruction = prompt.Trim() }
                    : action
            )
            .ToList();

        var ids = builtIn.Select(action => action.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var custom = settings.CustomRewriteActions
            .Where(action => !string.IsNullOrWhiteSpace(action.Name) && !string.IsNullOrWhiteSpace(action.Instruction))
            .Where(action => !string.IsNullOrWhiteSpace(action.Id) && ids.Add(action.Id))
            .Select(action => new RewriteAction(action.Id, "", action.Instruction.Trim()) { Title = action.Name.Trim() });

        All = [.. builtIn.Where(action => !action.IsMissing), .. custom, .. builtIn.Where(action => action.IsMissing)];
        Default = Find(settings.DefaultRewriteAction) is { IsFreeText: false } configured
            ? configured
            : Find(RewriteAction.Clearer.Id)!;
    }

    /// <summary>Every action, including the free-text <see cref="RewriteAction.Custom" />.</summary>
    public IReadOnlyList<RewriteAction> All { get; }

    public IEnumerable<RewriteAction> Pickable => All.Where(action => !action.IsFreeText);

    /// <summary>The action run on each capture; Clearer when the setting names no pickable action.</summary>
    public RewriteAction Default { get; }

    public RewriteAction? Find(string? id) =>
        All.FirstOrDefault(action => string.Equals(action.Id, id, StringComparison.OrdinalIgnoreCase));
}
