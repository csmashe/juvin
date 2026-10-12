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
    public void Load_PicksUpExternalChanges()
    {
        var path = _temp.File("settings.json");
        var service = new SettingsService(path);
        _ = service.Current;

        File.WriteAllText(path, """{ "historyRetentionDays": 7 }""");
        service.Load();

        Assert.Equal(7, service.Current.HistoryRetentionDays);
    }

    [Fact]
    public void RewriteActions_RoundTripThroughTheFile()
    {
        var path = _temp.File("settings.json");
        new SettingsService(path).Update(s => s with
        {
            RewriteInstructionOverrides = new Dictionary<string, string> { ["soften"] = "Be gentle." },
            CustomRewriteActions = [new CustomRewriteAction("custom-1", "Formal", "Make it formal.")],
            DefaultRewriteAction = "custom-1",
        });

        var loaded = new SettingsService(path).Current;

        Assert.Equal("Be gentle.", Assert.Single(loaded.RewriteInstructionOverrides, pair => pair.Key == "soften").Value);
        Assert.Equal(new CustomRewriteAction("custom-1", "Formal", "Make it formal."), Assert.Single(loaded.CustomRewriteActions));
        Assert.Equal("custom-1", loaded.DefaultRewriteAction);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "rewriteInstructionOverrides": null, "customRewriteActions": null }""")]
    [InlineData("""{ "rewriteInstructionOverrides": {}, "customRewriteActions": [] }""")]
    public void RewriteActions_LoadAsEmptyWhenMissingOrNull(string json)
    {
        var path = _temp.File("settings.json");
        File.WriteAllText(path, json);

        var loaded = new SettingsService(path).Current;

        Assert.Empty(loaded.RewriteInstructionOverrides);
        Assert.Empty(loaded.CustomRewriteActions);
        Assert.Equal("clearer", loaded.DefaultRewriteAction);
    }

    [Fact]
    public void Update_RaisesSettingsChangedWhenOnlyARewriteActionChanges()
    {
        var service = new SettingsService(_temp.File("settings.json"));
        var raised = 0;
        service.SettingsChanged += _ => raised++;

        service.Update(s => s with { RewriteInstructionOverrides = new Dictionary<string, string> { ["soften"] = "A" } });
        service.Update(s => s with { RewriteInstructionOverrides = new Dictionary<string, string> { ["soften"] = "A" } });
        service.Update(s => s with { RewriteInstructionOverrides = new Dictionary<string, string> { ["soften"] = "B" } });
        service.Update(s => s with { CustomRewriteActions = [new CustomRewriteAction("custom-1", "T", "I")] });
        service.Update(s => s with { CustomRewriteActions = [new CustomRewriteAction("custom-1", "T", "I")] });
        service.Update(s => s with { CustomRewriteActions = [new CustomRewriteAction("custom-1", "T", "Changed")] });

        Assert.Equal(4, raised);
    }
}
