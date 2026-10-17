using Avalonia.Input;
using Juvin.App.Services;
using Juvin.App.Tests.Fakes;
using Juvin.App.ViewModels;
using Juvin.Core.Hotkeys;
using Juvin.Core.Localization;
using Juvin.Platform.Hotkeys;

namespace Juvin.App.Tests;

public sealed class ShortcutPanelViewModelTests
{
    private readonly Localizer _localizer = new();
    private readonly FakeSettingsService _settings = new();

    [Fact]
    public void Recording_SavesTheChordInCanonicalForm()
    {
        var vm = Create();
        vm.BeginRecording();

        Assert.True(vm.HandleKey(Key.J, KeyModifiers.Control | KeyModifiers.Alt, PhysicalKey.None));

        Assert.Equal("Ctrl+Alt+J", _settings.Current.RewriteShortcut);
        Assert.False(vm.IsRecording);
        Assert.Equal("Ctrl+Alt+J", vm.RecorderText);
    }

    [Fact]
    public void Recording_SavesThePhysicalKeyWhenItDiffersFromTheLogicalOne()
    {
        var vm = Create();
        vm.BeginRecording();

        vm.HandleKey(Key.A, KeyModifiers.Control | KeyModifiers.Shift, PhysicalKey.Q);

        Assert.Equal("Ctrl+Shift+A", _settings.Current.RewriteShortcut);
        Assert.Equal("Q", _settings.Current.RewriteShortcutPhysicalKey);
    }

    [Fact]
    public void Recording_ClearsThePhysicalKeyWhenItMatchesTheLogicalOne()
    {
        _settings.Update(s => s with { RewriteShortcutPhysicalKey = "Q" });
        var vm = Create();
        vm.BeginRecording();

        vm.HandleKey(Key.J, KeyModifiers.Control, PhysicalKey.J);

        Assert.Equal("Ctrl+J", _settings.Current.RewriteShortcut);
        Assert.Null(_settings.Current.RewriteShortcutPhysicalKey);
    }

    [Fact]
    public void Recording_ShowsThePromptWhileRecording()
    {
        var vm = Create();

        vm.BeginRecording();

        Assert.Equal(_localizer["Shortcuts.Recorder.Prompt"], vm.RecorderText);
    }

    [Fact]
    public void Recording_OnMac_SwapsCommandAndControl()
    {
        var vm = Create(isMac: true);

        vm.BeginRecording();
        vm.HandleKey(Key.J, KeyModifiers.Meta | KeyModifiers.Shift, PhysicalKey.None);
        Assert.Equal("Ctrl+Shift+J", _settings.Current.RewriteShortcut);
        Assert.Equal("Cmd+Shift+J", vm.ShortcutText);

        vm.BeginRecording();
        vm.HandleKey(Key.K, KeyModifiers.Control, PhysicalKey.None);
        Assert.Equal("Super+K", _settings.Current.RewriteShortcut);
        Assert.Equal("Ctrl+K", vm.ShortcutText);
    }

    [Fact]
    public void Recording_OffMac_MapsMetaToSuper()
    {
        var vm = Create();
        vm.BeginRecording();

        vm.HandleKey(Key.F5, KeyModifiers.Meta, PhysicalKey.None);

        Assert.Equal("Super+F5", _settings.Current.RewriteShortcut);
    }

    [Theory]
    [InlineData(Key.J, KeyModifiers.None)]
    [InlineData(Key.CapsLock, KeyModifiers.Control)]
    [InlineData(Key.LeftCtrl, KeyModifiers.Control)]
    [InlineData(Key.LeftShift, KeyModifiers.Shift)]
    [InlineData(Key.A, KeyModifiers.Shift)]
    [InlineData(Key.Tab, KeyModifiers.Alt)]
    public void Recording_RefusesAKeyWithoutModifiersAModifierAloneOrAnUnusableKey(Key key, KeyModifiers modifiers)
    {
        var vm = Create();
        vm.BeginRecording();

        Assert.True(vm.HandleKey(key, modifiers, PhysicalKey.None));

        Assert.Equal(0, _settings.SaveCount);
        Assert.True(vm.IsRecording);
        Assert.True(vm.HasRecordHint);
    }

    [Theory]
    [InlineData(false, "Shortcuts.Recorder.NeedsModifier")]
    [InlineData(true, "Shortcuts.Recorder.NeedsModifierMac")]
    public void Recording_ShiftAlone_AsksForAnotherModifier(bool isMac, string hint)
    {
        var vm = Create(isMac: isMac);
        vm.BeginRecording();

        vm.HandleKey(Key.A, KeyModifiers.Shift, PhysicalKey.None);

        Assert.Equal(_localizer[hint], vm.RecordHint);
        Assert.Equal(0, _settings.SaveCount);
    }

    [Theory]
    [InlineData(false, Key.Tab, KeyModifiers.Alt)]
    [InlineData(false, Key.L, KeyModifiers.Meta)]
    [InlineData(false, Key.Delete, KeyModifiers.Control | KeyModifiers.Alt)]
    [InlineData(true, Key.Q, KeyModifiers.Meta)]
    [InlineData(true, Key.Space, KeyModifiers.Meta)]
    public void Recording_RefusesAShortcutTheSystemKeeps(bool isMac, Key key, KeyModifiers modifiers)
    {
        var vm = Create(isMac: isMac);
        vm.BeginRecording();

        vm.HandleKey(key, modifiers, PhysicalKey.None);

        Assert.Equal(_localizer["Shortcuts.Recorder.Reserved"], vm.RecordHint);
        Assert.True(vm.IsRecording);
        Assert.Equal(0, _settings.SaveCount);
    }

    [Fact]
    public void Recording_ReservedChordsDependOnTheOs()
    {
        var vm = Create();
        vm.BeginRecording();

        vm.HandleKey(Key.Q, KeyModifiers.Control, PhysicalKey.None);

        Assert.Equal("Ctrl+Q", _settings.Current.RewriteShortcut);
    }

    [Fact]
    public void Escape_CancelsWithoutSaving()
    {
        var vm = Create();
        vm.BeginRecording();

        Assert.True(vm.HandleKey(Key.Escape, KeyModifiers.Control, PhysicalKey.None));

        Assert.False(vm.IsRecording);
        Assert.Equal(0, _settings.SaveCount);
        Assert.Equal("Ctrl+Shift+F", vm.RecorderText);
    }

    [Fact]
    public void Keys_AreIgnoredWhenNotRecording()
    {
        var vm = Create();

        Assert.False(vm.HandleKey(Key.J, KeyModifiers.Control, PhysicalKey.None));
        Assert.Equal(0, _settings.SaveCount);
    }

    [Fact]
    public void BareTab_IsLeftForFocusNavigation()
    {
        var vm = Create();
        vm.BeginRecording();

        Assert.False(vm.HandleKey(Key.Tab, KeyModifiers.None, PhysicalKey.None));
        Assert.False(vm.HandleKey(Key.Tab, KeyModifiers.Shift, PhysicalKey.None));
        Assert.True(vm.IsRecording);
        Assert.Equal(0, _settings.SaveCount);
    }

    [Theory]
    [InlineData(Key.A, "A")]
    [InlineData(Key.Z, "Z")]
    [InlineData(Key.D0, "0")]
    [InlineData(Key.D9, "9")]
    [InlineData(Key.F1, "F1")]
    [InlineData(Key.F24, "F24")]
    [InlineData(Key.Back, "Backspace")]
    [InlineData(Key.PageDown, "PageDown")]
    [InlineData(Key.OemComma, "Comma")]
    [InlineData(Key.OemQuestion, "Slash")]
    [InlineData(Key.OemTilde, "Backquote")]
    public void KeyMap_NamesKeysAsTheChordDoes(Key key, string name)
    {
        Assert.Equal(name, ShortcutKeyMap.KeyName(key));
        Assert.True(KeyChord.TryCreate(ChordModifiers.Ctrl, name, out _));
    }

    [Theory]
    [InlineData(PhysicalKey.Q, "Q")]
    [InlineData(PhysicalKey.Digit0, "0")]
    [InlineData(PhysicalKey.Digit7, "7")]
    [InlineData(PhysicalKey.ArrowLeft, "Left")]
    [InlineData(PhysicalKey.Semicolon, "Semicolon")]
    [InlineData(PhysicalKey.F12, "F12")]
    [InlineData(PhysicalKey.None, null)]
    [InlineData(PhysicalKey.NumPad1, null)]
    [InlineData(PhysicalKey.IntlBackslash, null)]
    public void KeyMap_NamesPhysicalKeysAsTheChordDoes(PhysicalKey key, string? name)
    {
        Assert.Equal(name, ShortcutKeyMap.PhysicalKeyName(key));
    }

    [Fact]
    public void KeyMap_EveryMappedKeyIsAValidChordKey()
    {
        foreach (var name in Enum.GetValues<Key>().Select(ShortcutKeyMap.KeyName).OfType<string>())
        {
            Assert.Contains(name, KeyChord.KeyNames);
        }

        foreach (var name in Enum.GetValues<PhysicalKey>().Select(ShortcutKeyMap.PhysicalKeyName).OfType<string>())
        {
            Assert.Contains(name, KeyChord.KeyNames);
        }
    }

    [Fact]
    public void BackendOptions_ListAutomaticThenEachAvailableBackend()
    {
        var vm = Create(hotkeys: Hotkeys(new FakeHotkeyBackend("evdev"), new FakeHotkeyBackend("x11", isAvailable: false), new FakeHotkeyBackend("sharphook")));

        Assert.Equal([null, "evdev", "sharphook"], vm.BackendOptions.Select(option => option.Id));
        Assert.Equal(["Automatic", "Keyboard devices", "System keyboard hook"], vm.BackendOptions.Select(option => option.Name));
        Assert.Null(vm.SelectedBackend?.Id);
        Assert.Equal(0, _settings.SaveCount);
    }

    [Fact]
    public void PickingABackend_SavesItsId_AndAutomaticClearsIt()
    {
        var vm = Create(hotkeys: Hotkeys(new FakeHotkeyBackend("evdev"), new FakeHotkeyBackend("sharphook")));

        vm.SelectedBackend = vm.BackendOptions.Single(option => option.Id == "sharphook");
        Assert.Equal("sharphook", _settings.Current.HotkeyBackend);

        vm.SelectedBackend = vm.BackendOptions[0];
        Assert.Null(_settings.Current.HotkeyBackend);
    }

    [Fact]
    public async Task Status_NamesTheListeningBackend()
    {
        var hotkeys = Hotkeys(new FakeHotkeyBackend("evdev"));
        var vm = Create(hotkeys: hotkeys);

        await hotkeys.StartAsync(CancellationToken.None);

        Assert.True(vm.IsListening);
        Assert.Equal("Listening through Keyboard devices.", vm.StatusText);
    }

    [Fact]
    public async Task Status_ShowsTheBackendFailure()
    {
        var hotkeys = Hotkeys(new FakeHotkeyBackend("evdev") { StartFailure = "No keyboard access." });
        var vm = Create(hotkeys: hotkeys);

        await hotkeys.StartAsync(CancellationToken.None);

        Assert.False(vm.IsListening);
        Assert.Equal("Keyboard devices could not start: No keyboard access.", vm.StatusText);
    }

    [Fact]
    public async Task Status_ReportsABackendThatStoppedOnItsOwn()
    {
        var backend = new FakeHotkeyBackend("sharphook");
        var hotkeys = Hotkeys(backend);
        var vm = Create(hotkeys: hotkeys);
        await hotkeys.StartAsync(CancellationToken.None);

        backend.StopOnItsOwn("The keyboard hook stopped unexpectedly.");

        Assert.False(vm.IsListening);
        Assert.Equal("System keyboard hook stopped listening: The keyboard hook stopped unexpectedly. Juvin will try again.", vm.StatusText);
        await hotkeys.StopAsync();
    }

    [Fact]
    public async Task Status_ReportsNoBackend()
    {
        var hotkeys = Hotkeys();
        var vm = Create(hotkeys: hotkeys);

        await hotkeys.StartAsync(CancellationToken.None);

        Assert.Equal(_localizer["Shortcuts.Status.NoBackend"], vm.StatusText);
    }

    [Fact]
    public async Task Status_ExplainsThatAnInvalidSavedShortcutFallsBackToTheDefault()
    {
        _settings.Update(s => s with { RewriteShortcut = "Shift+A" });
        var hotkeys = Hotkeys(new FakeHotkeyBackend("evdev"));
        var vm = Create(hotkeys: hotkeys);

        await hotkeys.StartAsync(CancellationToken.None);

        Assert.True(vm.IsListening);
        Assert.Equal("Ctrl+Shift+F", vm.ShortcutText);
        Assert.Equal(_localizer.Format("Shortcuts.Status.InvalidShortcut", "Keyboard devices"), vm.StatusText);
    }

    [Fact]
    public async Task Status_KeepsAnUnavailableChoiceVisible_AndNamesTheBackendListeningInstead()
    {
        _settings.Update(s => s with { HotkeyBackend = "evdev" });
        var hotkeys = Hotkeys(new FakeHotkeyBackend("evdev", isAvailable: false), new FakeHotkeyBackend("sharphook"));
        var vm = Create(hotkeys: hotkeys);

        await hotkeys.StartAsync(CancellationToken.None);

        Assert.Equal([null, "sharphook", "evdev"], vm.BackendOptions.Select(option => option.Id));
        Assert.Equal("evdev", vm.SelectedBackend?.Id);
        Assert.Equal("Keyboard devices cannot run here, so Juvin listens through System keyboard hook instead.", vm.StatusText);
        Assert.Equal(1, _settings.SaveCount);

        vm.SelectedBackend = vm.BackendOptions[0];
        Assert.Equal([null, "sharphook"], vm.BackendOptions.Select(option => option.Id));
        Assert.Equal("Listening through System keyboard hook.", vm.StatusText);
    }

    [Fact]
    public async Task Press_ShowsDetected()
    {
        var backend = new FakeHotkeyBackend("evdev");
        var hotkeys = Hotkeys(backend);
        var vm = Create(hotkeys: hotkeys);
        await hotkeys.StartAsync(CancellationToken.None);

        backend.Press();

        Assert.True(vm.IsDetected);
    }

    [Fact]
    public async Task DesktopShortcutBackend_OffersRemovalAndTheSignInNote()
    {
        var backend = new FakeDesktopShortcutBackend(requiresSessionRestart: true);
        var hotkeys = Hotkeys(backend);
        var vm = Create(hotkeys: hotkeys);
        Assert.False(vm.ShowDesktopShortcut);

        await hotkeys.StartAsync(CancellationToken.None);
        Assert.True(vm.ShowDesktopShortcut);
        Assert.True(vm.ShowSignInNote);

        await vm.RemoveDesktopShortcutCommand.ExecuteAsync(null);
        Assert.Equal(1, backend.RemoveCount);
        // Removal stops the backend and hides the block; the message survives that refresh.
        Assert.False(vm.ShowDesktopShortcut);
        Assert.Equal(_localizer["Shortcuts.DesktopShortcut.RemovedUntilSignIn"], vm.DesktopShortcutMessage);
    }

    [Fact]
    public async Task DesktopShortcutBackend_HidesTheNoteWhenNoSignInIsNeeded()
    {
        var hotkeys = Hotkeys(new FakeDesktopShortcutBackend(requiresSessionRestart: false));
        var vm = Create(hotkeys: hotkeys);

        await hotkeys.StartAsync(CancellationToken.None);

        Assert.True(vm.ShowDesktopShortcut);
        Assert.False(vm.ShowSignInNote);

        await vm.RemoveDesktopShortcutCommand.ExecuteAsync(null);
        Assert.Equal(_localizer["Shortcuts.DesktopShortcut.Removed"], vm.DesktopShortcutMessage);
    }

    [Fact]
    public async Task Recording_SuspendsTheHotkey_UntilBlurEscapeOrSave()
    {
        var hotkeys = Hotkeys();
        var vm = Create(hotkeys: hotkeys);

        vm.BeginRecording();
        Assert.True(hotkeys.Suspended);
        vm.CancelRecording();
        Assert.False(hotkeys.Suspended);

        vm.BeginRecording();
        vm.HandleKey(Key.Escape, KeyModifiers.None, PhysicalKey.None);
        Assert.False(hotkeys.Suspended);

        vm.BeginRecording();
        vm.HandleKey(Key.J, KeyModifiers.Control, PhysicalKey.None);
        Assert.True(hotkeys.Suspended);
        await Task.Delay(ShortcutPanelViewModel.ResumeGrace * 3);
        Assert.True(hotkeys.Suspended);
        vm.HandleKeyUp(Key.LeftCtrl);
        vm.HandleKeyUp(Key.J);
        await Task.Delay(ShortcutPanelViewModel.ResumeGrace * 3);
        Assert.False(hotkeys.Suspended);
    }

    [Fact]
    public async Task ADisposedPanelsPendingResume_DoesNotResumeAnotherRecorder()
    {
        var hotkeys = Hotkeys();
        using var wizard = Create(hotkeys: hotkeys);
        using var settings = Create(hotkeys: hotkeys);

        wizard.BeginRecording();
        wizard.HandleKey(Key.J, KeyModifiers.Control, PhysicalKey.None);
        // ReSharper disable once DisposeOnUsingVariable -- the mid-test Dispose is the behaviour under test; the using only covers a failing assertion
        wizard.Dispose();
        Assert.False(hotkeys.Suspended);
        settings.BeginRecording();
        await Task.Delay(ShortcutPanelViewModel.ReleaseCap + ShortcutPanelViewModel.ResumeGrace);

        Assert.True(hotkeys.Suspended);
    }

    [Fact]
    public void Dispose_ResumesWhenClosedRightAfterTheKeyWasReleased()
    {
        var hotkeys = Hotkeys();
        using var vm = Create(hotkeys: hotkeys);
        vm.BeginRecording();
        vm.HandleKey(Key.J, KeyModifiers.Control, PhysicalKey.None);
        vm.HandleKeyUp(Key.J);
        Assert.True(hotkeys.Suspended);

        // ReSharper disable once DisposeOnUsingVariable -- the mid-test Dispose is the behaviour under test; the using only covers a failing assertion
        vm.Dispose();

        Assert.False(hotkeys.Suspended);
    }

    [Fact]
    public async Task ASecondRecording_OutlivesTheFirstSavesForcedResume()
    {
        var hotkeys = Hotkeys();
        var vm = Create(hotkeys: hotkeys);
        vm.BeginRecording();
        vm.HandleKey(Key.J, KeyModifiers.Control, PhysicalKey.None);
        vm.HandleKeyUp(Key.J);
        await Task.Delay(ShortcutPanelViewModel.ResumeGrace * 3);
        Assert.False(hotkeys.Suspended);

        vm.BeginRecording();
        vm.HandleKey(Key.K, KeyModifiers.Control, PhysicalKey.None);
        await Task.Delay(ShortcutPanelViewModel.ReleaseCap - ShortcutPanelViewModel.ResumeGrace);

        // The first save's cap has passed, but the second chord is still held.
        Assert.True(hotkeys.Suspended);
    }

    [Fact]
    public async Task Save_ResumesAnywayWhenTheKeyIsNeverReleased()
    {
        var hotkeys = Hotkeys();
        var vm = Create(hotkeys: hotkeys);

        vm.BeginRecording();
        vm.HandleKey(Key.J, KeyModifiers.Control, PhysicalKey.None);
        await Task.Delay(ShortcutPanelViewModel.ReleaseCap + ShortcutPanelViewModel.ResumeGrace);

        Assert.False(hotkeys.Suspended);
    }

    [Fact]
    public void Dispose_ResumesTheHotkey()
    {
        var hotkeys = Hotkeys();
        var vm = Create(hotkeys: hotkeys);
        vm.BeginRecording();

        vm.Dispose();

        Assert.False(hotkeys.Suspended);
    }

    private HotkeyService Hotkeys(params IHotkeyBackend[] backends) =>
        new(backends, _settings, new ConnectorService(new FakeConnectorSocket(), new FakeNativeHostRegistrar(), _settings, _localizer));

    private ShortcutPanelViewModel Create(bool isMac = false, HotkeyService? hotkeys = null) =>
        new(hotkeys ?? Hotkeys(), _settings, _localizer, action => action(), isMac);
}
