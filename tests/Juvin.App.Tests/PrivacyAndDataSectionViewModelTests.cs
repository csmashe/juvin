using Juvin.App.Tests.Fakes;
using Juvin.App.ViewModels.Sections;
using Juvin.Core.Localization;
using Juvin.Core.Models;

namespace Juvin.App.Tests;

public sealed class PrivacyAndDataSectionViewModelTests : IDisposable
{
    private readonly string _dataPath = Path.Join(Path.GetTempPath(), "juvin-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeCrashReporter _crashReporter = new();
    private readonly FakeHistoryService _history = new();
    private readonly Localizer _localizer = new();
    private readonly FakeLoginItem _loginItem = new();
    private readonly FakeSettingsService _settings = new();

    public void Dispose()
    {
        if (Directory.Exists(_dataPath))
        {
            Directory.Delete(_dataPath, recursive: true);
        }
    }

    [Fact]
    public void TheToggles_SaveTheSettings()
    {
        var vm = Create();

        vm.HistoryEnabled = true;
        vm.ProvenanceCaptureEnabled = true;
        vm.CrashReportingEnabled = false;

        Assert.True(_settings.Current.HistoryEnabled);
        Assert.True(_settings.Current.ProvenanceCaptureEnabled);
        Assert.False(_settings.Current.CrashReportingEnabled);
    }

    [Fact]
    public void CrashReporting_FollowsTheSavedSettingAndTheBuild()
    {
        _settings.Update(s => s with { CrashReportingEnabled = false });
        _crashReporter.IsAvailable = false;

        var vm = Create();

        Assert.False(vm.CrashReportingEnabled);
        Assert.False(vm.IsCrashReportingAvailable);
        _settings.Update(s => s with { CrashReportingEnabled = true });
        Assert.True(vm.CrashReportingEnabled);
    }

    [Fact]
    public void TheRetention_ShowsTheSavedPeriod()
    {
        _settings.Update(s => s with { HistoryRetentionDays = 0 });

        var vm = Create();

        Assert.Equal(["7 days", "30 days", "90 days", "1 year", "Keep forever"], vm.RetentionOptions.Select(o => o.Title));
        Assert.Equal(0, vm.SelectedRetention?.Days);
    }

    [Fact]
    public void ChangingTheRetention_SavesAndPrunes()
    {
        _history.Add(Entry("old", DateTimeOffset.UtcNow.AddDays(-10)));
        _history.Add(Entry("new", DateTimeOffset.UtcNow.AddDays(-1)));
        var vm = Create();

        vm.SelectedRetention = vm.RetentionOptions.Single(o => o.Days == 7);

        Assert.Equal(7, _settings.Current.HistoryRetentionDays);
        Assert.Equal(["new"], _history.Entries.Select(e => e.Id));
        Assert.Equal("1 rewrite kept", vm.HistorySummary);
    }

    [Fact]
    public void AFailedSave_RevertsTheToggle()
    {
        var vm = Create();
        _settings.UpdateFailure = new IOException("read-only");

        vm.HistoryEnabled = true;
        vm.SelectedRetention = vm.RetentionOptions.Single(o => o.Days == 7);

        Assert.False(vm.HistoryEnabled);
        Assert.Equal(30, vm.SelectedRetention?.Days);
        Assert.Empty(_history.Prunes);
    }

    [Fact]
    public void ClearHistory_AsksForConfirmationFirst()
    {
        _history.Add(Entry("a", DateTimeOffset.UtcNow));
        var vm = Create();
        Assert.True(vm.HasEntries);

        vm.RequestClearCommand.Execute(null);
        Assert.True(vm.IsConfirmingClear);
        vm.CancelClearCommand.Execute(null);
        Assert.False(vm.IsConfirmingClear);
        Assert.Single(_history.Entries);

        vm.RequestClearCommand.Execute(null);
        vm.ConfirmClearCommand.Execute(null);

        Assert.False(vm.IsConfirmingClear);
        Assert.Empty(_history.Entries);
        Assert.False(vm.HasEntries);
        Assert.Equal("0 rewrites kept", vm.HistorySummary);
    }

    [Fact]
    public void AFailedClear_ShowsTheError()
    {
        _history.Add(Entry("a", DateTimeOffset.UtcNow));
        _history.Failure = new IOException("locked");
        var vm = Create();

        vm.RequestClearCommand.Execute(null);
        vm.ConfirmClearCommand.Execute(null);

        Assert.True(vm.HasError);
        Assert.Contains("locked", vm.ErrorMessage);
    }

    [Fact]
    public void DeleteAll_ClearsHistoryResetsSettingsAndRemovesSecrets()
    {
        var secrets = Path.Join(_dataPath, "Secrets");
        Directory.CreateDirectory(secrets);
        File.WriteAllText(Path.Join(secrets, "codex"), "x");
        File.WriteAllText(Path.Join(_dataPath, "secret.key"), "k");
        _settings.Update(s => s with { HistoryEnabled = true, HasCompletedOnboarding = true, LocalModel = "llama3" });
        _history.Add(Entry("a", DateTimeOffset.UtcNow));
        var vm = Create();

        vm.RequestDeleteAllCommand.Execute(null);
        Assert.True(vm.IsConfirmingDeleteAll);
        vm.ConfirmDeleteAllCommand.Execute(null);

        Assert.Empty(_history.Entries);
        Assert.False(_settings.Current.HistoryEnabled);
        Assert.False(_settings.Current.HasCompletedOnboarding);
        Assert.Null(_settings.Current.LocalModel);
        Assert.False(vm.HistoryEnabled);
        Assert.Empty(Directory.EnumerateFileSystemEntries(secrets));
        Assert.False(File.Exists(Path.Join(_dataPath, "secret.key")));
        Assert.False(vm.HasError);
    }

    [Fact]
    public void AFailedDeleteAll_ShowsTheErrorAndKeepsTheSettings()
    {
        _settings.Update(s => s with { HistoryEnabled = true });
        var vm = Create();
        _settings.UpdateFailure = new UnauthorizedAccessException("denied");

        vm.RequestDeleteAllCommand.Execute(null);
        vm.ConfirmDeleteAllCommand.Execute(null);

        Assert.True(vm.HasError);
        Assert.True(vm.HistoryEnabled);
    }

    [Fact]
    public void DeleteAll_RemovesTheLoginItemWhenItIsOn()
    {
        _settings.Update(s => s with { LaunchAtLogin = true });
        var vm = Create();

        vm.RequestDeleteAllCommand.Execute(null);
        vm.ConfirmDeleteAllCommand.Execute(null);

        Assert.Equal(1, _loginItem.DisableCount);
        Assert.False(_settings.Current.LaunchAtLogin);
        Assert.False(vm.HasError);
    }

    [Fact]
    public void DeleteAll_LeavesTheLoginItemAloneWhenItIsOff()
    {
        var vm = Create();

        vm.RequestDeleteAllCommand.Execute(null);
        vm.ConfirmDeleteAllCommand.Execute(null);

        Assert.Equal(0, _loginItem.DisableCount);
    }

    [Fact]
    public void ARefusedLoginItemRemoval_StopsDeleteAllAndShowsTheError()
    {
        _settings.Update(s => s with { LaunchAtLogin = true, HistoryEnabled = true });
        _history.Add(Entry("a", DateTimeOffset.UtcNow));
        _loginItem.Fail = true;
        var vm = Create();

        vm.RequestDeleteAllCommand.Execute(null);
        vm.ConfirmDeleteAllCommand.Execute(null);

        Assert.True(vm.HasError);
        Assert.Single(_history.Entries);
        Assert.True(_settings.Current.LaunchAtLogin);
    }

    [Fact]
    public void AFailedDeleteAllAfterRemovingTheLoginItem_PutsItBack()
    {
        _settings.Update(s => s with { LaunchAtLogin = true });
        var vm = Create();
        _settings.UpdateFailure = new UnauthorizedAccessException("denied");

        vm.RequestDeleteAllCommand.Execute(null);
        vm.ConfirmDeleteAllCommand.Execute(null);

        Assert.True(vm.HasError);
        Assert.True(_settings.Current.LaunchAtLogin);
        Assert.Equal(1, _loginItem.DisableCount);
        Assert.True(_loginItem.Enabled);
    }

    [Fact]
    public void AnUnreadableHistory_IsReported()
    {
        _history.IsAvailable = false;

        var vm = Create();

        Assert.Equal(_localizer["PrivacyAndData.HistoryUnavailable"], vm.HistorySummary);
    }

    [Fact]
    public void Export_FormatsTheEntries()
    {
        _history.Add(Entry("a", DateTimeOffset.UtcNow));
        var vm = Create();

        Assert.Contains("### Rewrite", vm.ExportMarkdown());
        Assert.Contains("\"id\": \"a\"", vm.ExportJson());
    }

    private PrivacyAndDataSectionViewModel Create() => new(_settings, _history, _localizer, _loginItem, _crashReporter, _dataPath);

    private static HistoryEntry Entry(string id, DateTimeOffset createdAt) =>
        new() { Id = id, CreatedAt = createdAt, ActionTitle = "Make clearer", Draft = "draft", Result = "result" };
}
