using Juvin.App.Services;
using Juvin.App.Tests.Fakes;
using Juvin.App.ViewModels.Sections;
using Juvin.Core.Localization;
using Juvin.Platform;

namespace Juvin.App.Tests;

public sealed class GeneralSectionViewModelTests
{
    private readonly FakeSettingsService _settings = new();

    [Fact]
    public void CommittingASenderName_SavesItTrimmed()
    {
        var vm = Create();

        vm.CommitSenderNameCommand.Execute("  Chris  ");

        Assert.Equal("Chris", _settings.Current.SenderName);
    }

    [Fact]
    public void ABlankSenderName_SavesNull()
    {
        _settings.Update(s => s with { SenderName = "Chris" });
        var vm = Create();

        vm.SenderName = "   ";

        Assert.Null(_settings.Current.SenderName);
    }

    [Fact]
    public void SettingsChangedElsewhere_AreShownWithoutSavingAgain()
    {
        var vm = Create();

        _settings.Update(s => s with { SenderName = "Chris", CloseToTray = false });

        Assert.Equal(1, _settings.SaveCount);
        Assert.Equal("Chris", vm.SenderName);
        Assert.False(vm.CloseToTray);
    }

    [Fact]
    public void AFailedSave_RestoresTheSavedValue()
    {
        var vm = Create();
        _settings.UpdateFailure = new IOException("disk full");

        vm.SenderName = "Chris";

        Assert.Equal("", vm.SenderName);
        Assert.Null(_settings.Current.SenderName);
    }

    private GeneralSectionViewModel Create() => new(_settings, new TrayIconService(new Localizer(), new NoTrayHost()));

    private sealed class NoTrayHost : ITrayHostProbe
    {
        public bool IsTrayHostAvailable() => false;
    }
}
