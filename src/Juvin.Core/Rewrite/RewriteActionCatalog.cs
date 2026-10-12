using System.Diagnostics.CodeAnalysis;
using Juvin.Core.Models;

namespace Juvin.Core.Rewrite;

/// <summary>The actions as the user configured them: built-ins with any edited instruction, then their own actions.</summary>
public static class RewriteActionCatalog
{
    public const int MaxInstructionLength = 2000;
    public const int MaxTitleLength = 40;

    private const string CustomIdPrefix = "custom-";

    /// <summary>Every action, <see cref="RewriteAction.Custom" /> included; invalid saved entries are skipped.</summary>
    public static IReadOnlyList<RewriteAction> Resolve(AppSettings settings)
    {
        var actions = RewriteAction
            .BuiltIn.Select(action =>
                !action.IsCustom
                && settings.RewriteInstructionOverrides.TryGetValue(action.Id, out var instruction)
                && IsValidInstruction(instruction)
                    ? action with { Instruction = instruction.Trim() }
                    : action
            )
            .ToList();

        foreach (var custom in settings.CustomRewriteActions)
        {
            // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract -- a hand-edited file can hold null entries
            if (
                custom is null
                || string.IsNullOrWhiteSpace(custom.Id)
                || !IsValidTitle(custom.Title)
                || !IsValidInstruction(custom.Instruction)
                || actions.Any(action => string.Equals(action.Id, custom.Id, StringComparison.OrdinalIgnoreCase))
            )
            {
                continue;
            }

            actions.Add(new RewriteAction(custom.Id, "", custom.Instruction.Trim()) { Title = custom.Title.Trim() });
        }

        return actions;
    }

    public static RewriteAction? Find(AppSettings settings, string id) =>
        Resolve(settings).FirstOrDefault(action => string.Equals(action.Id, id, StringComparison.OrdinalIgnoreCase));

    public static bool IsValidInstruction([NotNullWhen(true)] string? instruction) =>
        !string.IsNullOrWhiteSpace(instruction) && instruction.Trim().Length <= MaxInstructionLength;

    public static bool IsValidTitle([NotNullWhen(true)] string? title) =>
        !string.IsNullOrWhiteSpace(title) && title.Trim().Length <= MaxTitleLength;

    /// <summary>A new id for a user-defined action; the prefix keeps it apart from every built-in id.</summary>
    public static string NewCustomId() => CustomIdPrefix + Guid.NewGuid().ToString("N")[..12];
}
