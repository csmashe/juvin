using System.Text.RegularExpressions;

namespace Juvin.Core.Rewrite;

/// <summary>
///     Replaces sender placeholders such as [Your Name] that a model leaves in a rewrite. Only a
///     placeholder standing alone on its line is filled, which is where a sign-off sits; one inside a
///     sentence may be addressed to the recipient. A bare [Name] is never filled for the same reason.
/// </summary>
internal static partial class PlaceholderFiller
{
    private const string Placeholder = @"(?:your[ \t]+(?:full[ \t]+)?name|sender(?:[ \t]+name)?|(?:your[ \t]+)?signature)";

    public static string Fill(string text, string? senderName)
    {
        if (string.IsNullOrWhiteSpace(senderName))
        {
            return text;
        }

        var name = senderName.Trim();
        return SignOffPlaceholder().Replace(text, match => match.Groups["lead"].Value + name);
    }

    /// <summary>
    ///     <see cref="Fill" /> for a partial reply: an unfinished last line that could still become a
    ///     placeholder line is held back, so each preview is a prefix of the filled final text and
    ///     append-only consumers print it correctly. Any other line streams as it arrives.
    /// </summary>
    public static string FillStreaming(string text, string? senderName)
    {
        if (string.IsNullOrWhiteSpace(senderName))
        {
            return text;
        }

        var lineStart = text.LastIndexOf('\n') + 1;
        var lead = text.AsSpan(lineStart).TrimStart(" \t-—");
        return Fill(lead.IsEmpty || lead[0] is '[' or '{' ? text[..lineStart] : text, senderName);
    }

    [GeneratedRegex(
        @"^(?<lead>[ \t]*(?:--?|—)?[ \t]*)(?:\{\{[ \t]*" + Placeholder + @"[ \t]*\}\}|\[[ \t]*" + Placeholder + @"[ \t]*\]|\{[ \t]*" + Placeholder + @"[ \t]*\})(?=[ \t]*\r?$)",
        RegexOptions.IgnoreCase | RegexOptions.Multiline
    )]
    private static partial Regex SignOffPlaceholder();
}
