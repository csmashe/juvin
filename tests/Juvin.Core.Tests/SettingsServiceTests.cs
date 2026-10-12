using Juvin.Core.Models;
using Juvin.Core.Services;

namespace Juvin.Core.Tests;

public sealed class SettingsServiceTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        _temp.Dispose();
    }

    [Fact]
    public void Current_ReturnsDefaultsWhenNoFileExists()
    {
        var service = new SettingsService(_temp.File("settings.json"));

        var settings = service.Current;

        Assert.Equal(ProcessingMode.Local, settings.ProcessingMode);
        Assert.Equal("http://localhost:11434", settings.LocalServerUrl);
        Assert.True(settings.LocalThinkingDisabled);
        Assert.Equal("Ctrl+Shift+F", settings.RewriteShortcut);
        Assert.False(settings.HistoryEnabled);
        Assert.Equal(30, settings.HistoryRetentionDays);
        Assert.False(settings.ProvenanceCaptureEnabled);
        Assert.False(settings.CrashReportingEnabled);
        Assert.True(settings.CloseToTray);
        Assert.Null(settings.UiLanguage);
        Assert.Equal(1, settings.SchemaVersion);
    }

    [Fact]
    public void Update_RaisesSettingsChangedOnlyWhenTheValueChanges()
    {
        var service = new SettingsService(_temp.File("settings.json"));
        var raised = new List<AppSettings>();
        service.SettingsChanged += raised.Add;

        service.Update(s => s with { ProcessingMode = ProcessingMode.Codex });
        service.Update(s => s with { ProcessingMode = ProcessingMode.Codex });

        var changed = Assert.Single(raised);
        Assert.Equal(ProcessingMode.Codex, changed.ProcessingMode);
    }

    [Fact]
    public void Update_PersistsCamelCaseJsonWithBackup()
    {
        var path = _temp.File("settings.json");
        var service = new SettingsService(path);

        service.Update(s => s with { LocalModel = "llama3" });
        service.Update(s => s with { HistoryEnabled = true });

        var json = File.ReadAllText(path);
        Assert.Contains("\"localModel\": \"llama3\"", json);
        Assert.Contains("\"processingMode\": \"Local\"", json);
        Assert.True(File.Exists(path + ".bak"));
        Assert.True(new SettingsService(path).Current.HistoryEnabled);
    }

    [Fact]
    public void RewriteActionSettings_RoundTrip()
    {
        var path = _temp.File("settings.json");
        var service = new SettingsService(path);

        service.Update(s =>
            s with
            {
                RewriteActionPrompts = new Dictionary<string, string> { ["soften"] = "Be kind." },
                CustomRewriteActions = [new CustomRewriteAction("custom-1a2b", "Pirate", "Talk like a pirate.")],
                RewriteLengthGuidance = LengthGuidance.SimilarLength,
            }
        );

        var json = File.ReadAllText(path);
        Assert.Contains("\"rewriteLengthGuidance\": \"SimilarLength\"", json);
        var loaded = new SettingsService(path).Current;
        Assert.Equal("Be kind.", loaded.RewriteActionPrompts["soften"]);
        Assert.Equal(new CustomRewriteAction("custom-1a2b", "Pirate", "Talk like a pirate."), Assert.Single(loaded.CustomRewriteActions));
        Assert.Equal(LengthGuidance.SimilarLength, loaded.RewriteLengthGuidance);
    }

    [Fact]
    public void RewriteActionSettings_DefaultToNothingConfigured()
    {
        var settings = new SettingsService(_temp.File("settings.json")).Current;

        Assert.Empty(settings.RewriteActionPrompts);
        Assert.Empty(settings.CustomRewriteActions);
        Assert.Equal(LengthGuidance.None, settings.RewriteLengthGuidance);
    }

    [Fact]
    public void Load_ToleratesUnknownAndMissingMembers()
    {
        var path = _temp.File("settings.json");
        File.WriteAllText(
            path,
            """{ "processingMode": "Codex", "someFutureSetting": { "nested": [1, 2] } }"""
        );

        var service = new SettingsService(path);

        Assert.Equal(ProcessingMode.Codex, service.Current.ProcessingMode);
        Assert.Equal("Ctrl+Shift+F", service.Current.RewriteShortcut);
    }

    [Fact]
    public void Load_ReadsNullActionSettingsAsEmpty()
    {
        var path = _temp.File("settings.json");
        File.WriteAllText(
            path,
            """{ "rewriteActionPrompts": null, "customRewriteActions": [null, { "id": "custom-1", "name": "Formal" }] }"""
        );

        var service = new SettingsService(path);

        Assert.Empty(service.Current.RewriteActionPrompts);
        var custom = Assert.Single(service.Current.CustomRewriteActions);
        Assert.Equal("custom-1", custom.Id);
        Assert.Equal("", custom.Instruction);
    }

    [Fact]
    public void Load_PicksUpExternalChanges()
    {
        var path = _temp.File("settings.json");
        var service = new SettingsService(path);
        _ = service.Current;

        File.WriteAllText(path, """{ "historyRetentionDays": 7 }""");
        service.Load();

        Assert.Equal(7, service.Current.HistoryRetentionDays);
    }
}
