using System.Globalization;
using Juvin.Core.Rewrite;

namespace Juvin.Core.Connector;

/// <summary>A capture turned into rewrite input, plus what Apply needs to find the same draft again.</summary>
/// <param name="EditorHash">Must still match the editor's text when the rewrite is applied.</param>
/// <param name="FormatHash">Set for a Markdown capture; the selection's formatting must still match it on apply.</param>
public sealed record CapturedContext(
    RewriteContext Context,
    string Adapter,
    string EditorId,
    string EditorHash,
    string? PageUrl,
    int SelectionStart,
    int SelectionEnd,
    string? FormatHash = null
)
{
    /// <exception cref="ArgumentException">The capture's completeness is not one of the protocol's values.</exception>
    public static CapturedContext From(ConnectorCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);

        if (!ConnectorProtocol.TryParseCompleteness(capture.Thread.Completeness, out var completeness))
        {
            throw new ArgumentException("The capture has an unknown completeness value.", nameof(capture));
        }

        var thread = capture.Thread.Messages
            .Select(message => new ThreadMessage(message.From, message.To ?? [], ParseSent(message.Sent), message.Body))
            .ToList();
        var markdown = capture.Selection.Markdown;
        var context = new RewriteContext(
            markdown ?? capture.Selection.Text,
            thread,
            capture.Subject,
            completeness,
            capture.Thread.CollapsedCount,
            markdown is not null
        );

        return new CapturedContext(
            context,
            capture.Adapter,
            capture.Editor.Id,
            capture.Editor.Hash,
            capture.PageUrl,
            capture.Selection.Start,
            capture.Selection.End,
            capture.Selection.FormatHash
        );
    }

    private static DateTimeOffset? ParseSent(string? sent) =>
        DateTimeOffset.TryParse(sent, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
            ? value
            : null;
}
