using System.Collections.Frozen;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using Juvin.Core.Rewrite;

namespace Juvin.Core.Connector;

public static class ConnectorMessageTypes
{
    public const string Hello = "hello";
    public const string Config = "config";
    public const string Ping = "ping";
    public const string Pong = "pong";
    public const string ShortcutPressed = "shortcut.pressed";
    public const string ContextRequest = "context.request";
    public const string ContextResponse = "context.response";
    public const string ApplyRequest = "apply.request";
    public const string ApplyResponse = "apply.response";
    public const string TestPageRequest = "testpage.request";
    public const string TestPageResponse = "testpage.response";
    public const string MenuShow = "menu.show";
    public const string MenuResponse = "menu.response";
    public const string MenuChoose = "menu.choose";
    public const string MenuUpdate = "menu.update";
    public const string MenuClose = "menu.close";
    public const string Error = "error";
}

public static class ApplyFormats
{
    public const string Markdown = "markdown";
}

/// <summary>The <c>menu.choose</c> actions that are not entries of the menu.</summary>
public static class MenuChoices
{
    /// <summary>Ask for a change, which carries the user's instruction; the same id as <see cref="RewriteAction.Custom" />.</summary>
    public const string Custom = "custom";

    public const string Review = "review";
    public const string Recapture = "recapture";
}

public static class MenuStates
{
    public const string Running = "running";
    public const string Applied = "applied";
    public const string Answer = "answer";
    public const string DraftChanged = "draft-changed";
    public const string Error = "error";
}

public static class ConnectorErrorCodes
{
    public const string BadMessage = "bad-message";
    public const string UnsupportedVersion = "unsupported-version";
    public const string UnknownType = "unknown-type";
    public const string HelloRequired = "hello-required";

    /// <summary>Not on the wire: the connection closed before a reply arrived.</summary>
    public const string Disconnected = "disconnected";

    /// <summary>Not on the wire: no response within the request timeout.</summary>
    public const string Timeout = "timeout";

    /// <summary>Not on the wire: the request would exceed the frame cap.</summary>
    public const string TooLarge = "too-large";

    /// <summary>Not on the wire: the request could not be sent.</summary>
    public const string Failed = "failed";

    /// <summary>Not on the wire: no browser is connected.</summary>
    public const string NoBrowser = "no-browser";

    /// <summary>Every code in <c>docs/connector-protocol.md</c>, the envelope codes above and those of each response, plus the app's own.</summary>
    public static readonly FrozenSet<string> All = FrozenSet.Create(
        StringComparer.Ordinal,
        BadMessage,
        UnsupportedVersion,
        UnknownType,
        HelloRequired,
        Disconnected,
        Timeout,
        TooLarge,
        Failed,
        NoBrowser,
        "no-active-tab",
        "no-adapter",
        "no-editor",
        "no-selection",
        "page-not-ready",
        "editor-gone",
        "browser-not-focused",
        "draft-changed",
        "unsupported-content",
        "unsupported",
        "internal"
    );
}

/// <summary>Makes peer-supplied values safe to trace: anything outside the spec's known values becomes <c>unknown</c>.</summary>
public static partial class ConnectorTrace
{
    private const string Unknown = "unknown";
    private const int MaxVersionLength = 32;

    private static readonly FrozenSet<string> s_browserNames = FrozenSet.Create(
        StringComparer.Ordinal,
        "firefox",
        "chrome",
        "edge",
        "chromium",
        "unknown"
    );

    /// <summary>The code when it is one of <see cref="ConnectorErrorCodes.All" />.</summary>
    public static string Code(string? code) => code is not null && ConnectorErrorCodes.All.Contains(code) ? code : Unknown;

    /// <summary>The <c>hello</c> browser name when it is one the spec lists.</summary>
    public static string BrowserName(string? name) => name is not null && s_browserNames.Contains(name) ? name : Unknown;

    /// <summary>A browser or extension version shaped like one: digits and dots, an optional short suffix, at most 32 characters.</summary>
    public static string Version(string? version) =>
        version is { Length: > 0 and <= MaxVersionLength } && VersionShape().IsMatch(version) ? version : Unknown;

    [GeneratedRegex(@"^[0-9]+(\.[0-9]+)*([.-][0-9A-Za-z.-]*)?\z")]
    private static partial Regex VersionShape();
}

/// <param name="Message">The parsed message, or null when <paramref name="Error" /> is set.</param>
/// <param name="Error">The reply to send back for a message that could not be accepted.</param>
public sealed record ConnectorParseResult(ConnectorMessage? Message, ErrorMessage? Error)
{
    /// <summary>The rejected frame declared itself an <c>error</c>, which is never answered with another.</summary>
    public bool RejectedErrorReply { get; init; }
}

/// <summary>Parses and serializes connector messages as specified in <c>docs/connector-protocol.md</c>.</summary>
public static class ConnectorProtocol
{
    public const int Version = 1;

    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        // Keeps non-ASCII text at its UTF-8 size so the frame cap is not reached early; frames are never embedded in HTML.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly Dictionary<string, ThreadCompleteness> s_completeness = new(StringComparer.Ordinal)
    {
        ["entire-thread"] = ThreadCompleteness.EntireThread,
        ["loaded-messages-only"] = ThreadCompleteness.LoadedMessagesOnly,
        ["visible-text-only"] = ThreadCompleteness.VisibleTextOnly,
        ["draft-only"] = ThreadCompleteness.DraftOnly,
        ["truncated"] = ThreadCompleteness.TruncatedForModelLimit,
    };

    public static string Serialize(ConnectorMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return JsonSerializer.Serialize(message, message.GetType(), s_json);
    }

    public static ConnectorParseResult Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return Reject(null, ConnectorErrorCodes.BadMessage, "The message is not valid JSON.");
        }

        using (document)
        {
            try
            {
                return ParseEnvelope(document.RootElement);
            }
            catch (InvalidOperationException)
            {
                // An unpaired surrogate passes JsonDocument.Parse but fails GetString.
                return Reject(null, ConnectorErrorCodes.BadMessage, "The message contains invalid text.", DeclaresError(document.RootElement));
            }
        }
    }

    private static ConnectorParseResult ParseEnvelope(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Reject(null, ConnectorErrorCodes.BadMessage, "The message is not a JSON object.");
        }

        var isError = DeclaresError(root);
        var id = root.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
            ? idElement.GetString()
            : null;

        if (!root.TryGetProperty("v", out var version) || version.ValueKind != JsonValueKind.Number)
        {
            return Reject(id, ConnectorErrorCodes.BadMessage, "The message has no numeric v.", isError);
        }

        if (!version.TryGetInt32(out var v) || v != Version)
        {
            return Reject(id, ConnectorErrorCodes.UnsupportedVersion, "Only protocol version 1 is supported.", isError);
        }

        if (!root.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
        {
            return Reject(id, ConnectorErrorCodes.BadMessage, "The message has no type.");
        }

        var messageType = MessageTypeFor(typeElement.GetString()!);
        if (messageType is null)
        {
            return Reject(id, ConnectorErrorCodes.UnknownType, "Unknown message type.");
        }

        ConnectorMessage? message;
        try
        {
            message = (ConnectorMessage?)root.Deserialize(messageType, s_json);
        }
        catch (JsonException)
        {
            message = null;
        }

        return message is { IsWellFormed: true }
            ? new ConnectorParseResult(message, null)
            : Reject(id, ConnectorErrorCodes.BadMessage, "The message is missing required fields.", isError);
    }

    /// <summary>A type that is not valid text is not <c>error</c>; the rejection that follows owes a reply.</summary>
    private static bool DeclaresError(JsonElement root)
    {
        try
        {
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                && type.ValueEquals(ConnectorMessageTypes.Error);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public static bool TryParseCompleteness(string value, out ThreadCompleteness completeness) =>
        s_completeness.TryGetValue(value, out completeness);

    public static ErrorMessage CreateError(string? id, string code, string message) =>
        new() { Id = id, Error = new ConnectorError(code, message) };

    private static ConnectorParseResult Reject(string? id, string code, string message, bool isError = false) =>
        new(null, CreateError(id, code, message)) { RejectedErrorReply = isError };

    private static Type? MessageTypeFor(string type) =>
        type switch
        {
            ConnectorMessageTypes.Hello => typeof(HelloMessage),
            ConnectorMessageTypes.Config => typeof(ConnectorConfig),
            ConnectorMessageTypes.Ping => typeof(PingMessage),
            ConnectorMessageTypes.Pong => typeof(PongMessage),
            ConnectorMessageTypes.ShortcutPressed => typeof(ShortcutPressedMessage),
            ConnectorMessageTypes.ContextRequest => typeof(ContextRequestMessage),
            ConnectorMessageTypes.ContextResponse => typeof(ContextResponseMessage),
            ConnectorMessageTypes.ApplyRequest => typeof(ApplyRequestMessage),
            ConnectorMessageTypes.ApplyResponse => typeof(ApplyResponseMessage),
            ConnectorMessageTypes.TestPageRequest => typeof(TestPageRequestMessage),
            ConnectorMessageTypes.TestPageResponse => typeof(TestPageResponseMessage),
            ConnectorMessageTypes.MenuShow => typeof(MenuShowMessage),
            ConnectorMessageTypes.MenuResponse => typeof(MenuResponseMessage),
            ConnectorMessageTypes.MenuChoose => typeof(MenuChooseMessage),
            ConnectorMessageTypes.MenuUpdate => typeof(MenuUpdateMessage),
            ConnectorMessageTypes.MenuClose => typeof(MenuCloseMessage),
            ConnectorMessageTypes.Error => typeof(ErrorMessage),
            _ => null,
        };
}
