using System.Globalization;
using System.Text.Json;
using Juvin.Core.Rewrite;

namespace Juvin.Cli;

/// <summary>
///     Reads the rewrite input: a JSON object with the draft and thread, or plain text taken as
///     the draft alone.
/// </summary>
internal static class StdinInput
{
    /// <exception cref="InvalidDataException">The JSON is malformed or has no draft.</exception>
    public static RewriteContext Parse(string text)
    {
        if (!text.TrimStart().StartsWith('{'))
        {
            return new RewriteContext(text.Trim(), [], null, ThreadCompleteness.DraftOnly, 0);
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            var draft = GetString(root, "draft") ?? throw new InvalidDataException("The input JSON has no \"draft\" string.");
            var thread = ParseThread(root);
            var collapsed = root.TryGetProperty("collapsed", out var c) && c.TryGetInt32(out var n) ? n : 0;
            var completeness =
                Enum.TryParse<ThreadCompleteness>(GetString(root, "completeness"), ignoreCase: true, out var stated)
                    ? stated
                    : thread.Count == 0
                        ? ThreadCompleteness.DraftOnly
                        : collapsed > 0
                            ? ThreadCompleteness.LoadedMessagesOnly
                            : ThreadCompleteness.EntireThread;
            return new RewriteContext(draft.Trim(), thread, GetString(root, "subject"), completeness, collapsed);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("The input is not valid rewrite JSON.", ex);
        }
    }

    private static List<ThreadMessage> ParseThread(JsonElement root)
    {
        if (!root.TryGetProperty("thread", out var array) && !root.TryGetProperty("messages", out array))
        {
            return [];
        }

        return
        [
            .. array
                .EnumerateArray()
                .Select(message => new ThreadMessage(
                    GetString(message, "from") ?? "",
                    ParseRecipients(message),
                    ParseDate(GetString(message, "sent") ?? GetString(message, "date")),
                    GetString(message, "body") ?? ""
                )),
        ];
    }

    private static string[] ParseRecipients(JsonElement message)
    {
        if (!message.TryGetProperty("to", out var to))
        {
            return [];
        }

        return to.ValueKind switch
        {
            JsonValueKind.Array => [.. to.EnumerateArray().Select(r => r.GetString()).OfType<string>()],
            JsonValueKind.String => [to.GetString()!],
            _ => [],
        };
    }

    private static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? date
            : null;

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
