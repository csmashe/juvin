using Juvin.App.Services;
using Juvin.App.Tests.Fakes;
using Juvin.App.ViewModels.Sections;
using Juvin.Core.Localization;
using Juvin.Platform;

namespace Juvin.App.Tests;

public sealed class GeneralSectionViewModelTests
{
    private readonly FakeSettingsService _settings = new();
    private readonly FakeLoginItem _loginItem = new();

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

    [Fact]
    public void AFailedSave_ShowsTheErrorUntilASaveSucceeds()
    {
        var vm = Create();
        _settings.UpdateFailure = new IOException("disk full");

        vm.CloseToTray = false;

        Assert.True(vm.HasSaveError);
        Assert.Contains("disk full", vm.SaveError);

        _settings.UpdateFailure = null;
        vm.CloseToTray = false;

        Assert.False(vm.HasSaveError);
        Assert.False(_settings.Current.CloseToTray);
    }

    [Fact]
    public void TurningLaunchAtLoginOn_EnablesTheLoginItemAndSaves()
    {
        var vm = Create();

        vm.LaunchAtLogin = true;

        Assert.True(_loginItem.Enabled);
        Assert.True(_settings.Current.LaunchAtLogin);
        Assert.False(vm.LaunchAtLoginFailed);
        Assert.True(vm.CanChangeLaunchAtLogin);
    }

    [Fact]
    public void TurningLaunchAtLoginOff_DisablesTheLoginItemAndSaves()
    {
        _settings.Update(s => s with { LaunchAtLogin = true });
        var vm = Create();

        vm.LaunchAtLogin = false;

        Assert.Equal(1, _loginItem.DisableCount);
        Assert.False(_settings.Current.LaunchAtLogin);
    }

    [Fact]
    public void AFailedLoginItemChange_RevertsTheSwitchAndDoesNotSave()
    {
        var vm = Create();
        _loginItem.Fail = true;

        vm.LaunchAtLogin = true;

        Assert.False(vm.LaunchAtLogin);
        Assert.True(vm.LaunchAtLoginFailed);
        Assert.Equal(new Localizer()["General.LaunchAtLoginFailed"], vm.LaunchAtLoginFailure);
        Assert.False(_settings.Current.LaunchAtLogin);
        Assert.Equal(0, _settings.SaveCount);
    }

    [Fact]
    public void ALoginItemFailureWithAUserMessage_ShowsThatMessage()
    {
        var vm = Create();
        _loginItem.Fail = true;
        _loginItem.FailureMessage = "Move Juvin to the Applications folder.";

        vm.LaunchAtLogin = true;

        Assert.True(vm.LaunchAtLoginFailed);
        Assert.Equal("Move Juvin to the Applications folder.", vm.LaunchAtLoginFailure);
    }

    [Fact]
    public void AFailedSaveAfterEnabling_RemovesTheLoginItemAgain()
    {
        var vm = Create();
        _settings.UpdateFailure = new IOException("disk full");

        vm.LaunchAtLogin = true;

        Assert.False(vm.LaunchAtLogin);
        Assert.True(vm.LaunchAtLoginFailed);
        Assert.False(_loginItem.Enabled);
        Assert.Equal(1, _loginItem.DisableCount);
    }

    [Fact]
    public void ASuccessfulChangeAfterAFailure_ClearsTheFailure()
    {
        var vm = Create();
        _loginItem.Fail = true;
        vm.LaunchAtLogin = true;
        _loginItem.Fail = false;

        vm.LaunchAtLogin = true;

        Assert.False(vm.LaunchAtLoginFailed);
        Assert.True(_settings.Current.LaunchAtLogin);
    }

    [Fact]
    public void AnUnsupportedLoginItem_DisablesTheSwitch()
    {
        _loginItem.IsSupported = false;

        var vm = Create();

        Assert.False(vm.IsLoginItemSupported);
        Assert.False(vm.CanChangeLaunchAtLogin);
    }

    [Fact]
    public void LaunchAtLoginChangedElsewhere_IsShownWithoutRegistering()
    {
        var vm = Create();

        _settings.Update(s => s with { LaunchAtLogin = true });

        Assert.True(vm.LaunchAtLogin);
        Assert.Equal(0, _loginItem.EnableCount);
        Assert.Equal(0, _loginItem.DisableCount);
    }

    private GeneralSectionViewModel Create() =>
        new(_settings, new TrayIconService(new Localizer(), new NoTrayHost()), _loginItem, new Localizer());

    private sealed class NoTrayHost : ITrayHostProbe
    {
        public Task<bool?> IsTrayHostAvailableAsync(CancellationToken ct) => Task.FromResult<bool?>(false);
    }
}
