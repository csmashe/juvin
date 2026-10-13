using Juvin.Core.Localization;
using Juvin.Core.Models;
using Juvin.Core.Providers;
using Juvin.Core.Rewrite;

namespace Juvin.App.ViewModels;

/// <summary>The context chips the panel and the action menu's status line share.</summary>
internal static class CaptureLabels
{
    public static string MessageCount(int count, Localizer localizer) =>
        count == 1 ? localizer["Panel.MessageCountOne"] : localizer.Format("Panel.MessageCount", count);

    public static string Completeness(ThreadCompleteness completeness, int collapsedCount, Localizer localizer) =>
        completeness switch
        {
            ThreadCompleteness.EntireThread => localizer["Panel.Completeness.EntireThread"],
            ThreadCompleteness.LoadedMessagesOnly when collapsedCount > 0 =>
                localizer.Format("Panel.Completeness.LoadedWithCollapsed", collapsedCount),
            ThreadCompleteness.LoadedMessagesOnly => localizer["Panel.Completeness.LoadedMessagesOnly"],
            ThreadCompleteness.VisibleTextOnly => localizer["Panel.Completeness.VisibleTextOnly"],
            ThreadCompleteness.DraftOnly => localizer["Panel.Completeness.DraftOnly"],
            _ => localizer["Panel.Completeness.Truncated"],
        };

    /// <summary>"Local", "Private server", or "Codex: cloud"; before a run, with no provider yet, from the settings alone.</summary>
    public static string Provider(IModelProvider? provider, string? modelId, AppSettings settings, Localizer localizer)
    {
        if ((provider?.Mode ?? settings.ProcessingMode) == ProcessingMode.Codex)
        {
            return localizer["Panel.Provider.Codex"];
        }

        var model = modelId ?? settings.LocalModel ?? localizer["Panel.Provider.DefaultModel"];
        var runsLocally = provider?.RunsLocally ?? OpenAiCompatibleProvider.IsLoopbackServer(settings.LocalServerUrl);
        return runsLocally == false
            ? localizer.Format("Panel.Provider.PrivateServer", model)
            : localizer.Format("Panel.Provider.Local", model);
    }
}
