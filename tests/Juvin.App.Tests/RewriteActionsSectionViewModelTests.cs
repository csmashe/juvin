using Juvin.App.Tests.Fakes;
using Juvin.App.ViewModels.Sections;
using Juvin.Core.Localization;
using Juvin.Core.Models;
using Juvin.Core.Rewrite;

namespace Juvin.App.Tests;

public sealed class RewriteActionsSectionViewModelTests
{
    private readonly FakeSettingsService _settings = new();

    [Fact]
    public void Load_ShowsTheBuiltInsWithoutTheFreeTextActionAndTheSavedChoices()
    {
        _settings.Update(s =>
            s with
            {
                RewriteActionPrompts = new Dictionary<string, string> { ["grammar"] = "Fix typos." },
                DefaultRewriteAction = "missing",
                RewriteLengthGuidance = LengthGuidance.Shorter,
            }
        );

        var saves = _settings.SaveCount;

        var vm = Create();

        Assert.Equal(saves, _settings.SaveCount);
        Assert.Equal(["Soften", "Make clearer", "Make shorter", "Fix grammar", "What am I missing?"], vm.BuiltInActions.Select(row => row.Title));
        var grammar = vm.BuiltInActions.Single(row => row.Id == "grammar");
        Assert.Equal("Fix typos.", grammar.Instruction);
        Assert.True(grammar.IsEdited);
        Assert.False(vm.BuiltInActions.Single(row => row.Id == "soften").IsEdited);
        Assert.Equal("missing", vm.SelectedDefault?.Id);
        Assert.Equal(LengthGuidance.Shorter, vm.SelectedLengthGuidance?.Value);
    }

    [Fact]
    public void EditingAPrompt_SavesItTrimmed()
    {
        var vm = Create();

        vm.BuiltInActions.Single(row => row.Id == "soften").Instruction = "  Be kind. ";

        Assert.Equal("Be kind.", _settings.Current.RewriteActionPrompts["soften"]);
        Assert.Equal("Be kind.", new RewriteActionCatalog(_settings.Current).Find("soften")?.Instruction);
    }

    [Fact]
    public void Reset_RestoresTheDefaultAndRemovesTheOverride()
    {
        var vm = Create();
        var soften = vm.BuiltInActions.Single(row => row.Id == "soften");
        soften.Instruction = "Be kind.";

        soften.ResetCommand.Execute(null);

        Assert.Equal(RewriteAction.Soften.Instruction, soften.Instruction);
        Assert.False(soften.IsEdited);
        Assert.Empty(_settings.Current.RewriteActionPrompts);
    }

    [Fact]
    public void ABlankPrompt_StoresNothingAndShowsTheDefault()
    {
        var vm = Create();
        var shorter = vm.BuiltInActions.Single(row => row.Id == "shorter");
        shorter.Instruction = "Cut it.";

        shorter.Instruction = "   ";

        Assert.Empty(_settings.Current.RewriteActionPrompts);
        Assert.Equal(RewriteAction.Shorter.Instruction, shorter.Instruction);
    }

    [Fact]
    public void AddingACustomAction_SavesAPlaceholderThatIsNotOfferedUntilFilledIn()
    {
        var vm = Create();

        vm.AddCustomActionCommand.Execute(null);

        var saved = Assert.Single(_settings.Current.CustomRewriteActions);
        Assert.StartsWith("custom-", saved.Id);
        Assert.Equal("New action", saved.Name);
        var row = Assert.Single(vm.CustomActions);
        Assert.Equal(saved.Id, row.Id);
        Assert.DoesNotContain(vm.DefaultOptions, option => option.Id == saved.Id);

        row.Instruction = " Talk like a pirate. ";
        row.Name = "Pirate";

        Assert.Equal(new CustomRewriteAction(saved.Id, "Pirate", "Talk like a pirate."), Assert.Single(_settings.Current.CustomRewriteActions));
        Assert.Same(row, Assert.Single(vm.CustomActions));
        Assert.Contains(vm.DefaultOptions, option => option is { Title: "Pirate" } && option.Id == saved.Id);
    }

    [Fact]
    public void RemovingTheDefaultCustomAction_RemovesItAndResetsTheDefault()
    {
        _settings.Update(s =>
            s with
            {
                CustomRewriteActions = [new CustomRewriteAction("custom-a", "Pirate", "Arr."), new CustomRewriteAction("custom-b", "Formal", "Sir.")],
                DefaultRewriteAction = "custom-a",
            }
        );
        var vm = Create();
        Assert.Equal("custom-a", vm.SelectedDefault?.Id);

        vm.CustomActions[0].RemoveCommand.Execute(null);

        Assert.Equal("custom-b", Assert.Single(_settings.Current.CustomRewriteActions).Id);
        Assert.Equal("clearer", _settings.Current.DefaultRewriteAction);
        Assert.Equal("custom-b", Assert.Single(vm.CustomActions).Id);
        Assert.Equal("clearer", vm.SelectedDefault?.Id);
    }

    [Fact]
    public void TheDefaultPicker_ListsPickableActionsAndSavesTheChoice()
    {
        var vm = Create();

        Assert.Equal(["soften", "clearer", "shorter", "grammar", "missing"], vm.DefaultOptions.Select(option => option.Id));
        vm.SelectedDefault = vm.DefaultOptions.Single(option => option.Id == "shorter");

        Assert.Equal("shorter", _settings.Current.DefaultRewriteAction);
    }

    [Fact]
    public void TheLengthPicker_SavesTheChoice()
    {
        var vm = Create();

        vm.SelectedLengthGuidance = vm.LengthGuidanceOptions.Single(option => option.Value == LengthGuidance.SimilarLength);

        Assert.Equal(LengthGuidance.SimilarLength, _settings.Current.RewriteLengthGuidance);
    }

    [Fact]
    public void ChangesMadeElsewhere_AreShownWithoutSavingAgain()
    {
        var vm = Create();
        var saves = _settings.SaveCount;

        _settings.Update(s => s with { RewriteActionPrompts = new Dictionary<string, string> { ["clearer"] = "Be clear." } });

        Assert.Equal("Be clear.", vm.BuiltInActions.Single(row => row.Id == "clearer").Instruction);
        Assert.Equal(saves + 1, _settings.SaveCount);
    }

    [Fact]
    public void AFailedSave_ShowsTheStoredValueAgain()
    {
        var vm = Create();
        _settings.UpdateFailure = new IOException("disk full");

        vm.BuiltInActions.Single(row => row.Id == "soften").Instruction = "Be kind.";

        Assert.Equal(RewriteAction.Soften.Instruction, vm.BuiltInActions.Single(row => row.Id == "soften").Instruction);
    }

    private RewriteActionsSectionViewModel Create() => new(_settings, new Localizer());
}
