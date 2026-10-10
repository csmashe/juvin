using System.Text.Json;
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
                return Reject(null, ConnectorErrorCodes.BadMessage, "The message contains invalid text.");
            }
        }
    }

    private static ConnectorParseResult ParseEnvelope(JsonElement root)
    {
        {
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Reject(null, ConnectorErrorCodes.BadMessage, "The message is not a JSON object.");
            }

            var id = root.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
                ? idElement.GetString()
                : null;

            if (!root.TryGetProperty("v", out var version) || version.ValueKind != JsonValueKind.Number)
            {
                return Reject(id, ConnectorErrorCodes.BadMessage, "The message has no numeric v.");
            }

            if (!version.TryGetInt32(out var v) || v != Version)
            {
                return Reject(id, ConnectorErrorCodes.UnsupportedVersion, "Only protocol version 1 is supported.");
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
                : Reject(id, ConnectorErrorCodes.BadMessage, "The message is missing required fields.") with
                {
                    RejectedErrorReply = messageType == typeof(ErrorMessage),
                };
        }
    }

    public static bool TryParseCompleteness(string value, out ThreadCompleteness completeness) =>
        s_completeness.TryGetValue(value, out completeness);

    public static ErrorMessage CreateError(string? id, string code, string message) =>
        new() { Id = id, Error = new ConnectorError(code, message) };

    private static ConnectorParseResult Reject(string? id, string code, string message) =>
        new(null, CreateError(id, code, message));

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
            ConnectorMessageTypes.Error => typeof(ErrorMessage),
            _ => null,
        };
}
