using Juvin.Core.Models;
using Juvin.Core.Rewrite;

namespace Juvin.Core.Tests;

public sealed class RewriteActionCatalogTests
{
    [Fact]
    public void Defaults_AreTheBuiltInActions()
    {
        var catalog = new RewriteActionCatalog(new AppSettings());

        Assert.Equal(RewriteAction.BuiltIn, catalog.All);
        Assert.Equal(["soften", "clearer", "shorter", "grammar", "missing"], catalog.Pickable.Select(a => a.Id));
        Assert.Same(RewriteAction.Clearer, catalog.Default);
    }

    [Fact]
    public void AnEditedPrompt_ReplacesTheBuiltInInstruction()
    {
        var catalog = new RewriteActionCatalog(
            new AppSettings { RewriteActionPrompts = new Dictionary<string, string> { ["soften"] = "  Be gentle. " } }
        );

        Assert.Equal("Be gentle.", catalog.Find("SOFTEN")?.Instruction);
        Assert.Equal("Action.Soften", catalog.Find("soften")?.TitleKey);
        Assert.Equal(RewriteAction.Clearer.Instruction, catalog.Find("clearer")?.Instruction);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n")]
    public void ABlankPrompt_UsesTheBuiltInInstruction(string prompt)
    {
        var catalog = new RewriteActionCatalog(
            new AppSettings { RewriteActionPrompts = new Dictionary<string, string> { ["shorter"] = prompt } }
        );

        Assert.Equal(RewriteAction.Shorter.Instruction, catalog.Find("shorter")?.Instruction);
    }

    [Fact]
    public void ThePromptOfTheFreeTextAction_IsIgnored()
    {
        var catalog = new RewriteActionCatalog(
            new AppSettings { RewriteActionPrompts = new Dictionary<string, string> { ["custom"] = "Sneaky." } }
        );

        Assert.Equal("", catalog.Find("custom")?.Instruction);
    }

    [Fact]
    public void CustomActions_ComeAfterTheBuiltInsAndBeforeMissing()
    {
        var catalog = new RewriteActionCatalog(
            new AppSettings
            {
                CustomRewriteActions =
                [
                    new CustomRewriteAction("custom-a", " Pirate ", " Talk like a pirate. "),
                    new CustomRewriteAction("custom-b", "Formal", "Make it formal."),
                ],
            }
        );

        Assert.Equal(
            ["soften", "clearer", "shorter", "grammar", "custom-a", "custom-b", "missing"],
            catalog.Pickable.Select(a => a.Id)
        );
        var pirate = catalog.Find("custom-a")!;
        Assert.Equal("Pirate", pirate.Title);
        Assert.Equal("Talk like a pirate.", pirate.Instruction);
        Assert.False(pirate.IsFreeText);
    }

    [Fact]
    public void IncompleteCollidingAndDuplicateCustomActions_AreLeftOut()
    {
        var catalog = new RewriteActionCatalog(
            new AppSettings
            {
                CustomRewriteActions =
                [
                    new CustomRewriteAction("custom-a", "", "No name."),
                    new CustomRewriteAction("custom-b", "No instruction", "  "),
                    new CustomRewriteAction("Clearer", "Impostor", "Replace clearer."),
                    new CustomRewriteAction("custom-c", "First", "One."),
                    new CustomRewriteAction("custom-c", "Second", "Two."),
                ],
            }
        );

        Assert.Equal(["custom-c"], catalog.All.Where(a => a.Title is not null).Select(a => a.Id));
        Assert.Equal("First", catalog.Find("custom-c")?.Title);
        Assert.Same(RewriteAction.Clearer, catalog.Find("clearer"));
    }

    [Theory]
    [InlineData("custom")]
    [InlineData("no-such-action")]
    [InlineData("custom-gone")]
    public void TheDefault_FallsBackToClearer(string configured)
    {
        var catalog = new RewriteActionCatalog(new AppSettings { DefaultRewriteAction = configured });

        Assert.Equal("clearer", catalog.Default.Id);
    }

    [Fact]
    public void TheDefault_CanBeACustomActionAndCarriesItsInstruction()
    {
        var catalog = new RewriteActionCatalog(
            new AppSettings
            {
                DefaultRewriteAction = "custom-a",
                CustomRewriteActions = [new CustomRewriteAction("custom-a", "Pirate", "Arr.")],
            }
        );

        Assert.Equal("Arr.", catalog.Default.Instruction);
    }

    [Fact]
    public void TheDefaultClearer_CarriesItsEditedPrompt()
    {
        var catalog = new RewriteActionCatalog(
            new AppSettings { RewriteActionPrompts = new Dictionary<string, string> { ["clearer"] = "Be clear." } }
        );

        Assert.Equal("Be clear.", catalog.Default.Instruction);
    }

    [Fact]
    public void NewIds_NeverCollideWithBuiltInsOrTakenIds()
    {
        var taken = new List<string>();
        for (var i = 0; i < 200; i++)
        {
            taken.Add(CustomRewriteAction.NewId(taken));
        }

        Assert.Equal(taken.Count, taken.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(taken, id => Assert.StartsWith("custom-", id));
        Assert.DoesNotContain(taken, id => RewriteAction.BuiltIn.Any(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase)));
    }
}
