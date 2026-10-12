using Juvin.Core.Localization;
using Juvin.Core.Models;
using Juvin.Core.Rewrite;

namespace Juvin.Core.Tests;

public sealed class RewriteActionCatalogTests
{
    private static readonly AppSettings s_settings = new()
    {
        RewriteInstructionOverrides = new Dictionary<string, string> { ["soften"] = "  Be gentle.  ", ["custom"] = "Ignored." },
        CustomRewriteActions = [new CustomRewriteAction("custom-1", " Formal ", "Make it formal.")],
    };

    [Fact]
    public void Resolve_ListsTheBuiltInsThenTheUsersActions()
    {
        Assert.Equal(
            ["soften", "clearer", "shorter", "grammar", "custom", "missing", "custom-1"],
            RewriteActionCatalog.Resolve(s_settings).Select(action => action.Id)
        );
    }

    [Fact]
    public void Resolve_AppliesEditedInstructionsToBuiltInsOnly()
    {
        var actions = RewriteActionCatalog.Resolve(s_settings);

        Assert.Equal("Be gentle.", actions.Single(action => action.Id == "soften").Instruction);
        Assert.Equal(RewriteAction.Clearer, actions.Single(action => action.Id == "clearer"));
        Assert.Same(RewriteAction.Custom, actions.Single(action => action.IsCustom));
    }

    [Fact]
    public void Resolve_NamesUserActionsLiterally()
    {
        var localizer = new Localizer();
        var formal = RewriteActionCatalog.Resolve(s_settings)[^1];

        Assert.Equal("Formal", formal.DisplayName(localizer));
        Assert.Equal("Make it formal.", formal.Instruction);
        Assert.Equal("Soften", RewriteAction.Soften.DisplayName(localizer));
    }

    [Fact]
    public void Resolve_SkipsInvalidAndCollidingEntries()
    {
        var settings = new AppSettings
        {
            RewriteInstructionOverrides = new Dictionary<string, string> { ["clearer"] = "   " },
            CustomRewriteActions =
            [
                new CustomRewriteAction("soften", "Clash", "Clashes with a built-in."),
                new CustomRewriteAction("custom-1", "", "No title."),
                new CustomRewriteAction("custom-2", "No instruction", " "),
                new CustomRewriteAction("custom-3", "Kept", "Kept."),
                new CustomRewriteAction("CUSTOM-3", "Duplicate", "Duplicate."),
            ],
        };

        var actions = RewriteActionCatalog.Resolve(settings);

        Assert.Equal(RewriteAction.Clearer.Instruction, actions.Single(action => action.Id == "clearer").Instruction);
        Assert.Equal("Kept", Assert.Single(actions, action => action.Title is not null).Title);
    }

    [Fact]
    public void Find_IsCaseInsensitiveAndNullForAnUnknownId()
    {
        Assert.Equal("Be gentle.", RewriteActionCatalog.Find(s_settings, "SOFTEN")?.Instruction);
        Assert.Equal("custom-1", RewriteActionCatalog.Find(s_settings, "Custom-1")?.Id);
        Assert.Same(RewriteAction.Missing, RewriteActionCatalog.Find(new AppSettings(), "missing"));
        Assert.Null(RewriteActionCatalog.Find(s_settings, "nope"));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("  ", false)]
    [InlineData("Do it.", true)]
    public void IsValidInstruction_NeedsText(string? instruction, bool valid)
    {
        Assert.Equal(valid, RewriteActionCatalog.IsValidInstruction(instruction));
    }

    [Fact]
    public void IsValidInstruction_AllowsUpToTheLimitAfterTrimming()
    {
        var longest = new string('a', RewriteActionCatalog.MaxInstructionLength);

        Assert.True(RewriteActionCatalog.IsValidInstruction($"  {longest}  "));
        Assert.False(RewriteActionCatalog.IsValidInstruction(longest + "a"));
    }

    [Fact]
    public void IsValidTitle_NeedsTextUpToTheLimitAfterTrimming()
    {
        var longest = new string('a', RewriteActionCatalog.MaxTitleLength);

        Assert.False(RewriteActionCatalog.IsValidTitle(" "));
        Assert.True(RewriteActionCatalog.IsValidTitle($" {longest} "));
        Assert.False(RewriteActionCatalog.IsValidTitle(longest + "a"));
    }

    [Fact]
    public void NewCustomId_NeverMatchesABuiltInAndIsUnique()
    {
        var ids = Enumerable.Range(0, 100).Select(_ => RewriteActionCatalog.NewCustomId()).ToList();

        Assert.Equal(100, ids.Distinct().Count());
        Assert.All(ids, id => Assert.DoesNotContain(RewriteAction.BuiltIn, action => string.Equals(action.Id, id, StringComparison.OrdinalIgnoreCase)));
    }
}
