using System.Globalization;
using System.Text;
using System.Text.Json;
using Juvin.Core.Models;
using Juvin.Core.Providers;

namespace Juvin.Core.Services;

/// <summary>Formats history for a file the user saves. The labels are part of the file format, so they are not localized.</summary>
public static class HistoryExport
{
    private static readonly JsonSerializerOptions s_indented = new(HistoryService.JsonOptions) { WriteIndented = true };

    public static string ToJson(IReadOnlyList<HistoryEntry> entries) => JsonSerializer.Serialize(entries, s_indented);

    public static string ToMarkdown(IReadOnlyList<HistoryEntry> entries)
    {
        var builder = new StringBuilder("# Juvin rewrite history\n");
        foreach (var entry in entries)
        {
            var where = entry.Mode == ProcessingMode.Codex ? "Cloud" : entry.RanLocally ? "Local" : "Private server";
            builder
                .Append(CultureInfo.InvariantCulture, $"\n## {entry.CreatedAt:yyyy-MM-dd HH:mm:ss zzz} - {Escape(entry.ActionTitle)}\n\n")
                .Append(CultureInfo.InvariantCulture, $"- Subject: {(string.IsNullOrWhiteSpace(entry.Subject) ? "(none)" : Escape(entry.Subject))}\n")
                .Append(CultureInfo.InvariantCulture, $"- Provider: {Escape(entry.ProviderName)}, model {Escape(entry.ModelId)} ({where})\n")
                .Append(CultureInfo.InvariantCulture, $"- Applied: {(entry.Applied ? "yes" : "no")}\n");
            AppendBlock(builder, "Draft", entry.Draft);
            AppendBlock(builder, "Rewrite", entry.Result);
            if (entry.SystemPrompt is not null)
            {
                AppendBlock(builder, "System prompt", entry.SystemPrompt);
            }

            foreach (var message in entry.MessagesSent ?? [])
            {
                AppendBlock(builder, message.Role == ChatRole.User ? "Prompt" : "Earlier reply", message.Content);
            }
        }

        return builder.ToString();
    }

    /// <summary>Metadata sits outside the fences, so a subject must not become a link, an image, or HTML.</summary>
    private static string Escape(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            switch (c)
            {
                case '\n' or '\r':
                    builder.Append(' ');
                    break;
                case '\\' or '`' or '*' or '_' or '[' or ']' or '<' or '>' or '!' or '#' or '(' or ')' or '|':
                    builder.Append('\\').Append(c);
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>Fenced so that headings or lists in the email cannot change the document's structure.</summary>
    private static void AppendBlock(StringBuilder builder, string heading, string text)
    {
        var fence = new string('`', Math.Max(3, LongestBacktickRun(text) + 1));
        builder.Append(CultureInfo.InvariantCulture, $"\n### {heading}\n\n{fence}text\n{text}\n{fence}\n");
    }

    private static int LongestBacktickRun(string text)
    {
        int longest = 0, current = 0;
        foreach (var c in text)
        {
            current = c == '`' ? current + 1 : 0;
            longest = Math.Max(longest, current);
        }

        return longest;
    }
}
