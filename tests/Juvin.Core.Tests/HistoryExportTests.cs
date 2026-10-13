using System.Text.Json;
using System.Text.Json.Serialization;
using Juvin.Core.Models;
using Juvin.Core.Providers;
using Juvin.Core.Rewrite;
using Juvin.Core.Services;

namespace Juvin.Core.Tests;

public sealed class HistoryExportTests
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly HistoryEntry s_local = new()
    {
        Id = "a",
        CreatedAt = new DateTimeOffset(2026, 10, 12, 9, 5, 7, TimeSpan.Zero),
        ActionId = "clearer",
        ActionTitle = "Make clearer",
        Subject = "Re: timeline",
        Adapter = "gmail",
        Draft = "hi dana\n# not a heading",
        Result = "Hi Dana, ``` here",
        Mode = ProcessingMode.Local,
        ProviderName = "Local model",
        ModelId = "qwen3",
        RanLocally = true,
        ThreadMessageCount = 3,
        Completeness = ThreadCompleteness.EntireThread,
        Duration = TimeSpan.FromSeconds(2),
        Applied = true,
    };

    private static readonly HistoryEntry s_codex = s_local with
    {
        Id = "b",
        Subject = null,
        Mode = ProcessingMode.Codex,
        ProviderName = "Codex",
        ModelId = "gpt",
        RanLocally = false,
    };

    [Fact]
    public void ToMarkdown_HasAHeadingAndBothTextsPerEntry()
    {
        var markdown = HistoryExport.ToMarkdown([s_local, s_codex]);

        Assert.StartsWith("# Juvin rewrite history\n", markdown);
        Assert.Contains("## 2026-10-12 09:05:07 +00:00 - Make clearer", markdown);
        Assert.Contains("- Subject: Re: timeline", markdown);
        Assert.Contains("- Subject: (none)", markdown);
        Assert.Contains("- Provider: Local model, model qwen3 (Local)", markdown);
        Assert.Contains("- Provider: Codex, model gpt (Cloud)", markdown);
        Assert.Contains("### Draft\n\n```text\nhi dana\n# not a heading\n```\n", markdown);
        Assert.Contains("### Rewrite\n\n````text\nHi Dana, ``` here\n````\n", markdown);
        Assert.DoesNotContain("### Prompt", markdown);
    }

    [Fact]
    public void ToMarkdown_EscapesMetadataOutsideTheFences()
    {
        var entry = s_local with { Subject = "![pixel](https://x.example/p)\n<b>hi</b>", ActionTitle = "*Shout*" };

        var markdown = HistoryExport.ToMarkdown([entry]);

        Assert.Contains("- Subject: \\!\\[pixel\\]\\(https://x.example/p\\) \\<b\\>hi\\</b\\>\n", markdown);
        Assert.Contains(" - \\*Shout\\*\n", markdown);
    }

    [Fact]
    public void ToMarkdown_IncludesCapturedPrompts()
    {
        var entry = s_local with
        {
            SystemPrompt = "You rewrite email.",
            MessagesSent =
            [
                new ChatMessage(ChatRole.User, "Make it clearer: hi dana"),
                new ChatMessage(ChatRole.Assistant, "Hi Dana"),
                new ChatMessage(ChatRole.User, "Shorter"),
            ],
        };

        var markdown = HistoryExport.ToMarkdown([entry]);

        Assert.Contains("### System prompt\n\n```text\nYou rewrite email.\n```\n", markdown);
        Assert.Contains("### Prompt\n\n```text\nMake it clearer: hi dana\n```\n", markdown);
        Assert.Contains("### Earlier reply\n\n```text\nHi Dana\n```\n", markdown);
        Assert.Contains("### Prompt\n\n```text\nShorter\n```\n", markdown);
    }

    [Fact]
    public void ToJson_RoundTrips()
    {
        var json = HistoryExport.ToJson([s_local, s_codex]);

        Assert.Equal([s_local, s_codex], JsonSerializer.Deserialize<List<HistoryEntry>>(json, s_options));
        Assert.Contains("\n  {", json);
        Assert.Contains("\"mode\": \"Codex\"", json);
    }
}
