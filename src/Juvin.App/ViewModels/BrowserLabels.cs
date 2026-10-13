using Juvin.Core.Localization;

namespace Juvin.App.ViewModels;

internal static class BrowserLabels
{
    /// <summary>The display name for the lowercase browser name an extension reports in its hello.</summary>
    public static string Describe(string name, Localizer localizer) =>
        name switch
        {
            "firefox" => localizer["BrowserConnector.Browser.Firefox"],
            "chrome" => localizer["BrowserConnector.Browser.Chrome"],
            "edge" => localizer["BrowserConnector.Browser.Edge"],
            "chromium" => localizer["BrowserConnector.Browser.Chromium"],
            _ => localizer["BrowserConnector.Browser.Unknown"],
        };
}
