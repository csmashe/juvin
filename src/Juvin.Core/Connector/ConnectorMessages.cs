// ReSharper disable UnusedAutoPropertyAccessor.Global -- read and written by System.Text.Json
// ReSharper disable NotAccessedPositionalProperty.Global -- read and written by System.Text.Json
// ReSharper disable ClassNeverInstantiated.Global -- instantiated by System.Text.Json
// ReSharper disable UnusedMember.Global -- Version is serialized; CapturedAt is read once the rewrite panel lands

using System.Text.Json.Serialization;

namespace Juvin.Core.Connector;

/// <summary>Envelope shared by every message in <c>docs/connector-protocol.md</c>.</summary>
public abstract record ConnectorMessage
{
    protected ConnectorMessage(string type)
    {
        Type = type;
    }

    [JsonPropertyName("v")]
    [JsonPropertyOrder(-3)]
    public int Version { get; init; } = ConnectorProtocol.Version;

    [JsonPropertyOrder(-2)]
    public string Type { get; }

    /// <summary>Set on requests and echoed by their responses; null on notifications.</summary>
    [JsonPropertyOrder(-1)]
    public string? Id { get; init; }

    /// <summary>Checks the rules the JSON shape alone cannot express.</summary>
    internal virtual bool IsWellFormed => true;
}

public sealed record ConnectorError(string Code, string Message);

public sealed record ExtensionIdentity(string Id, string Version);

public sealed record BrowserIdentity(string Name, string Version);

public sealed record ConnectorAction(string Id, string Title);

/// <param name="Mode"><c>local</c> or <c>codex</c>.</param>
/// <param name="ModelReachable">Null until the app has probed the model.</param>
public sealed record ConnectorHealth(
    string Mode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] bool? ModelReachable
);

public sealed record HelloMessage() : ConnectorMessage(ConnectorMessageTypes.Hello)
{
    public required ExtensionIdentity Extension { get; init; }

    public required BrowserIdentity Browser { get; init; }
}

/// <summary>Everything the extension needs from the app; sent after <c>hello</c> and on every settings change.</summary>
public sealed record ConnectorConfig() : ConnectorMessage(ConnectorMessageTypes.Config)
{
    public required IReadOnlyList<ConnectorAction> Actions { get; init; }

    public required IReadOnlyList<string> Adapters { get; init; }

    public required string Shortcut { get; init; }

    public required ConnectorHealth Health { get; init; }
}

public sealed record PingMessage() : ConnectorMessage(ConnectorMessageTypes.Ping)
{
    internal override bool IsWellFormed => Id is not null;
}

public sealed record PongMessage() : ConnectorMessage(ConnectorMessageTypes.Pong)
{
    internal override bool IsWellFormed => Id is not null;
}

public sealed record ShortcutPressedMessage() : ConnectorMessage(ConnectorMessageTypes.ShortcutPressed);

public sealed record ContextRequestMessage() : ConnectorMessage(ConnectorMessageTypes.ContextRequest)
{
    /// <summary>Recapture from the tab that owns this editor; null captures the shortcut's tab.</summary>
    public string? EditorId { get; init; }

    /// <summary>
    ///     True for the desktop hotkey, which asks every connected browser: one with no focused window
    ///     answers <c>browser-not-focused</c> instead of reading its last active tab. Omitted otherwise.
    /// </summary>
    public bool? RequireFocus { get; init; }

    internal override bool IsWellFormed => Id is not null;
}

public sealed record ContextResponseMessage() : ConnectorMessage(ConnectorMessageTypes.ContextResponse)
{
    public required bool Ok { get; init; }

    public ConnectorCapture? Capture { get; init; }

    public ConnectorError? Error { get; init; }

    internal override bool IsWellFormed =>
        Id is not null
        && (Ok
            ? Capture is not null
                && ConnectorProtocol.TryParseCompleteness(Capture.Thread.Completeness, out _)
                // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract -- System.Text.Json does not enforce element nullability
                && Capture.Thread.Messages.All(m => m is not null)
            : Error is not null);
}

public sealed record ApplyRequestMessage() : ConnectorMessage(ConnectorMessageTypes.ApplyRequest)
{
    public required string EditorId { get; init; }

    public required string ExpectedHash { get; init; }

    public required int Start { get; init; }

    public required int End { get; init; }

    public required string Text { get; init; }

    internal override bool IsWellFormed => Id is not null && Start >= 0 && End >= Start;
}

public sealed record ApplyResponseMessage() : ConnectorMessage(ConnectorMessageTypes.ApplyResponse)
{
    public required bool Ok { get; init; }

    public ConnectorError? Error { get; init; }

    internal override bool IsWellFormed => Id is not null && (Ok || Error is not null);
}

public sealed record TestPageRequestMessage() : ConnectorMessage(ConnectorMessageTypes.TestPageRequest)
{
    internal override bool IsWellFormed => Id is not null;
}

public sealed record TestPageResponseMessage() : ConnectorMessage(ConnectorMessageTypes.TestPageResponse)
{
    public required bool Ok { get; init; }

    public ConnectorError? Error { get; init; }

    internal override bool IsWellFormed => Id is not null && (Ok || Error is not null);
}

public sealed record ErrorMessage() : ConnectorMessage(ConnectorMessageTypes.Error)
{
    public required ConnectorError Error { get; init; }
}

/// <summary>A successful capture. Optional members are the ones a page may not have.</summary>
public sealed record ConnectorCapture
{
    public required string Adapter { get; init; }

    public string? PageUrl { get; init; }

    public string? CapturedAt { get; init; }

    public string? Subject { get; init; }

    public required CapturedEditor Editor { get; init; }

    public required CapturedSelection Selection { get; init; }

    public required CapturedThread Thread { get; init; }
}

/// <param name="Id">Token the content script assigned to the editor; passed back on apply.</param>
/// <param name="Hash"><c>sha256:</c> plus the hex digest of the editor's full plain text at capture time.</param>
public sealed record CapturedEditor(string Id, string Hash);

/// <param name="Start">Character offset of the selection within the editor's plain text.</param>
public sealed record CapturedSelection(string Text, int Start, int End);

public sealed record CapturedThread
{
    public required string Completeness { get; init; }

    public int CollapsedCount { get; init; }

    /// <summary>Oldest first.</summary>
    public required IReadOnlyList<CapturedMessage> Messages { get; init; }
}

public sealed record CapturedMessage
{
    public required string From { get; init; }

    public IReadOnlyList<string>? To { get; init; }

    public string? Sent { get; init; }

    public required string Body { get; init; }
}
