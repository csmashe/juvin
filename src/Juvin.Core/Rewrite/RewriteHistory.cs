using Juvin.Core.Connector;
using Juvin.Core.Localization;
using Juvin.Core.Models;

namespace Juvin.Core.Rewrite;

public static class RewriteHistory
{
    /// <summary>The history entry for a finished rewrite. The prompt and messages are kept only while provenance capture is on.</summary>
    public static HistoryEntry CreateEntry(
        RewriteAction action,
        CapturedContext capture,
        RewriteResult result,
        AppSettings settings,
        Localizer localizer
    )
    {
        var provenance = result.Provenance;
        return new HistoryEntry
        {
            Id = HistoryEntry.NewId(),
            CreatedAt = DateTimeOffset.UtcNow,
            ActionId = action.Id,
            ActionTitle = action.DisplayName(localizer),
            Subject = capture.Context.Subject,
            Adapter = capture.Adapter,
            Draft = capture.Context.Draft,
            Result = result.Text,
            Mode = provenance.Mode,
            ProviderName = provenance.ProviderName,
            ModelId = provenance.ModelId,
            RanLocally = provenance.RanLocally,
            ThreadMessageCount = provenance.ThreadMessageCount,
            Completeness = provenance.Completeness,
            Duration = provenance.Duration,
            // The session captured with the settings it was created from; consent is checked again now.
            SystemPrompt = settings.ProvenanceCaptureEnabled ? provenance.SystemPromptSent : null,
            MessagesSent = settings.ProvenanceCaptureEnabled ? provenance.MessagesSent : null,
        };
    }
}
