using Juvin.App.Services;
using Juvin.Core.Connector;

namespace Juvin.App.ViewModels;

/// <summary>The string table key for a connector error code; unknown codes get a generic message.</summary>
internal static class ConnectorErrorLabels
{
    public static string KeyFor(string? code) =>
        code switch
        {
            "no-active-tab" => "Panel.Error.NoActiveTab",
            "no-adapter" => "Panel.Error.NoAdapter",
            "no-editor" => "Panel.Error.NoEditor",
            "no-selection" => "Panel.Error.NoSelection",
            "page-not-ready" => "Panel.Error.PageNotReady",
            "editor-gone" => "Panel.Error.EditorGone",
            ConnectorService.BrowserNotFocusedCode => "Panel.Error.BrowserNotFocused",
            ConnectorService.NoBrowserCode => "Panel.Error.NoBrowser",
            ConnectorErrorCodes.Disconnected => "Panel.Error.Disconnected",
            ConnectorRewriteTarget.TimeoutCode => "Panel.Error.Timeout",
            ConnectorRewriteTarget.TooLargeCode => "Panel.Error.TooLarge",
            _ => "Panel.Error.Unknown",
        };
}
