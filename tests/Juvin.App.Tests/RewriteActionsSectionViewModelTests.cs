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
    public void TheRows_AreTheBuiltInsWithoutCustom()
    {
        var vm = Create();

        Assert.Equal(["soften", "clearer", "shorter", "grammar", "missing"], vm.BuiltInActions.Select(row => row.Id));
        Assert.Equal("Soften", vm.BuiltInActions[0].Title);
        Assert.Equal(RewriteAction.Soften.Instruction, vm.BuiltInActions[0].Instruction);
        Assert.False(vm.BuiltInActions[0].HasHint);
        Assert.True(Row(vm, "missing").HasHint);
        Assert.All(vm.BuiltInActions, row => Assert.False(row.IsEdited));
    }

    [Fact]
    public void EditingAnInstruction_SavesItTrimmed()
    {
        var vm = Create();

        Row(vm, "soften").Instruction = "  Be gentle.  ";

        Assert.Equal("Be gentle.", _settings.Current.RewriteInstructionOverrides["soften"]);
        Assert.Equal("Be gentle.", Row(vm, "soften").Instruction);
        Assert.True(Row(vm, "soften").IsEdited);
    }

    [Fact]
    public void EditingBackToTheDefault_RemovesTheOverride()
    {
        var vm = Create();
        Row(vm, "soften").Instruction = "Be gentle.";

        Row(vm, "soften").Instruction = RewriteAction.Soften.Instruction + " ";

        Assert.Empty(_settings.Current.RewriteInstructionOverrides);
        Assert.False(Row(vm, "soften").IsEdited);
    }

    [Fact]
    public void Reset_RestoresTheDefault()
    {
        var vm = Create();
        Row(vm, "grammar").Instruction = "Fix it.";

        Row(vm, "grammar").ResetCommand.Execute(null);

        Assert.Empty(_settings.Current.RewriteInstructionOverrides);
        Assert.Equal(RewriteAction.Grammar.Instruction, Row(vm, "grammar").Instruction);
        Assert.False(Row(vm, "grammar").IsEdited);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData(null)]
    public void AnInvalidInstruction_IsKeptButNotSaved(string? instruction)
    {
        var text = instruction ?? new string('a', RewriteActionCatalog.MaxInstructionLength + 1);
        var vm = Create();
        Row(vm, "soften").Instruction = "Be gentle.";
        var saves = _settings.SaveCount;

        Row(vm, "soften").Instruction = text;

        Assert.Equal(saves, _settings.SaveCount);
        Assert.Equal("Be gentle.", _settings.Current.RewriteInstructionOverrides["soften"]);
        Assert.True(Row(vm, "soften").IsInvalid);
        Assert.Equal(text, Row(vm, "soften").Instruction);

        Row(vm, "clearer").Instruction = "Clear it up.";

        Assert.Equal(text, Row(vm, "soften").Instruction);

        Row(vm, "soften").ResetCommand.Execute(null);

        Assert.False(Row(vm, "soften").IsInvalid);
        Assert.False(_settings.Current.RewriteInstructionOverrides.ContainsKey("soften"));
    }

    [Fact]
    public void AnAddedAction_IsSavedOnceItHasATitleAndAnInstruction()
    {
        var vm = Create();

        vm.AddCustomActionCommand.Execute(null);
        var row = Assert.Single(vm.CustomActions);
        row.CommitTitleCommand.Execute(" Formal ");

        Assert.Empty(_settings.Current.CustomRewriteActions);
        Assert.Same(row, Assert.Single(vm.CustomActions));
        Assert.False(row.IsInstructionInvalid);

        row.Instruction = "Make it formal.";

        Assert.Equal(new CustomRewriteAction(row.Id, "Formal", "Make it formal."), Assert.Single(_settings.Current.CustomRewriteActions));
        Assert.StartsWith("custom-", row.Id);
        Assert.Same(row, Assert.Single(vm.CustomActions));
    }

    [Fact]
    public void EditingASavedActionToBlank_KeepsTheSavedVersion()
    {
        var vm = CreateWithFormal();
        var row = Assert.Single(vm.CustomActions);

        row.Title = " ";

        Assert.True(row.IsTitleInvalid);
        Assert.Equal("Formal", Assert.Single(_settings.Current.CustomRewriteActions).Title);
        Assert.Equal(" ", row.Title);

        row.Title = "Very formal";

        Assert.False(row.IsTitleInvalid);
        Assert.Equal("Very formal", Assert.Single(_settings.Current.CustomRewriteActions).Title);
    }

    [Fact]
    public void AnOverLongTitle_IsNotSaved()
    {
        var vm = CreateWithFormal();
        var row = Assert.Single(vm.CustomActions);

        row.Title = new string('a', RewriteActionCatalog.MaxTitleLength + 1);

        Assert.True(row.IsTitleInvalid);
        Assert.Equal("Formal", Assert.Single(_settings.Current.CustomRewriteActions).Title);
    }

    [Fact]
    public void RemovingAnAction_DeletesIt()
    {
        var vm = CreateWithFormal();

        vm.CustomActions[0].RemoveCommand.Execute(null);

        Assert.Empty(vm.CustomActions);
        Assert.Empty(_settings.Current.CustomRewriteActions);
    }

    [Fact]
    public void RemovingAnUnsavedAction_DoesNotSave()
    {
        var vm = Create();
        vm.AddCustomActionCommand.Execute(null);

        vm.CustomActions[0].RemoveCommand.Execute(null);

        Assert.Empty(vm.CustomActions);
        Assert.Equal(0, _settings.SaveCount);
    }

    [Fact]
    public void AnExternalChange_RefreshesTheRows()
    {
        var vm = Create();
        vm.AddCustomActionCommand.Execute(null);
        var unfinished = vm.CustomActions[0];

        _settings.Update(s => s with
        {
            RewriteInstructionOverrides = new Dictionary<string, string> { ["shorter"] = "Halve it." },
            CustomRewriteActions = [new CustomRewriteAction("custom-1", "Formal", "Make it formal.")],
        });

        Assert.Equal("Halve it.", Row(vm, "shorter").Instruction);
        Assert.True(Row(vm, "shorter").IsEdited);
        Assert.Equal([unfinished.Id, "custom-1"], vm.CustomActions.Select(row => row.Id));
        Assert.Equal("Formal", vm.CustomActions[1].Title);
    }

    [Fact]
    public void ASaveFailure_KeepsTheDraftsAndRetrySavesThem()
    {
        var vm = CreateWithFormal();
        _settings.UpdateFailure = new IOException("disk full");

        Row(vm, "soften").Instruction = "Be gentle.";
        vm.CustomActions[0].Title = "Renamed";
        vm.AddCustomActionCommand.Execute(null);
        vm.CustomActions[1].Title = "Brief";
        vm.CustomActions[1].Instruction = "Keep it brief.";

        Assert.True(vm.HasSaveError);
        Assert.Contains("disk full", vm.SaveError);
        Assert.Equal("Be gentle.", Row(vm, "soften").Instruction);
        Assert.Equal(["Renamed", "Brief"], vm.CustomActions.Select(row => row.Title));
        Assert.Empty(_settings.Current.RewriteInstructionOverrides);
        Assert.Equal("Formal", _settings.Current.CustomRewriteActions[0].Title);

        _settings.UpdateFailure = null;
        vm.RetrySaveCommand.Execute(null);

        Assert.False(vm.HasSaveError);
        Assert.Equal("Be gentle.", _settings.Current.RewriteInstructionOverrides["soften"]);
        Assert.Equal(["Renamed", "Brief"], _settings.Current.CustomRewriteActions.Select(action => action.Title));
    }

    [Fact]
    public void AnUnrelatedSettingsChange_KeepsTheUnsavedDrafts()
    {
        var vm = CreateWithFormal();
        _settings.UpdateFailure = new IOException("disk full");
        Row(vm, "soften").Instruction = "Be gentle.";
        vm.CustomActions[0].Title = "Renamed";
        vm.AddCustomActionCommand.Execute(null);
        vm.CustomActions[1].Title = "Brief";
        vm.CustomActions[1].Instruction = "Keep it brief.";
        _settings.UpdateFailure = null;

        _settings.Update(s => s with { CloseToTray = false });

        Assert.True(vm.HasSaveError);
        Assert.Equal("Be gentle.", Row(vm, "soften").Instruction);
        Assert.Equal(["Renamed", "Brief"], vm.CustomActions.Select(row => row.Title));
    }

    private static BuiltInActionRow Row(RewriteActionsSectionViewModel vm, string id) =>
        vm.BuiltInActions.Single(row => row.Id == id);

    private RewriteActionsSectionViewModel CreateWithFormal()
    {
        _settings.Update(s => s with { CustomRewriteActions = [new CustomRewriteAction("custom-1", "Formal", "Make it formal.")] });
        return Create();
    }

    private RewriteActionsSectionViewModel Create() => new(_settings, new Localizer());
}
