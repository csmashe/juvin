using Juvin.Core.Localization;

namespace Juvin.App.ViewModels;

internal static class AdapterLabels
{
    public static string Describe(string adapter, Localizer localizer) =>
        adapter switch
        {
            "gmail" => localizer["Panel.Adapter.Gmail"],
            "outlook" => localizer["Panel.Adapter.Outlook"],
            "generic" => localizer["Panel.Adapter.Generic"],
            "test-page" => localizer["Panel.Adapter.TestPage"],
            _ => adapter,
        };
}
