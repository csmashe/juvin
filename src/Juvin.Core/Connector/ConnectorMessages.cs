// ReSharper disable UnusedAutoPropertyAccessor.Global -- read and written by System.Text.Json
// ReSharper disable NotAccessedPositionalProperty.Global -- read and written by System.Text.Json
// ReSharper disable ClassNeverInstantiated.Global -- instantiated by System.Text.Json
// ReSharper disable UnusedMember.Global -- Version is serialized; CapturedAt is carried for completeness and the CLI or history may use it

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

/// <param name="Code">What the UI maps to its own localized text.</param>
/// <param name="Message">For logs only on the app side; it is never shown, because the UI maps by <paramref name="Code" />.</param>
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

    // ReSharper disable ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract -- System.Text.Json does not enforce element nullability
    internal override bool IsWellFormed => Actions.All(action => action is not null) && Adapters.All(adapter => adapter is not null);
    // ReSharper restore ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
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

    // ReSharper disable ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract -- System.Text.Json does not enforce element nullability
    internal override bool IsWellFormed =>
        Id is not null
        && (Ok
            ? Capture is not null
                && Capture.Selection.Start >= 0
                && Capture.Selection.End >= Capture.Selection.Start
                && Capture.Selection.Markdown is null == Capture.Selection.FormatHash is null
                && ConnectorProtocol.TryParseCompleteness(Capture.Thread.Completeness, out _)
                && Capture.Thread.CollapsedCount >= 0
                && Capture.Thread.Messages.All(m => m is not null && (m.To is null || m.To.All(to => to is not null)))
            : Error is not null);
    // ReSharper restore ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
}

public sealed record ApplyRequestMessage() : ConnectorMessage(ConnectorMessageTypes.ApplyRequest)
{
    public required string EditorId { get; init; }

    public required string ExpectedHash { get; init; }

    public required int Start { get; init; }

    public required int End { get; init; }

    public required string Text { get; init; }

    /// <summary><see cref="ApplyFormats.Markdown" />, or null for plain text.</summary>
    public string? Format { get; init; }

    /// <summary>The capture's <see cref="CapturedSelection.FormatHash" />; required with <see cref="ApplyFormats.Markdown" />.</summary>
    public string? ExpectedFormatHash { get; init; }

    internal override bool IsWellFormed =>
        Id is not null
        && Start >= 0
        && End >= Start
        && (Format is null || (Format == ApplyFormats.Markdown && ExpectedFormatHash is not null));
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

public sealed record MenuShowMessage() : ConnectorMessage(ConnectorMessageTypes.MenuShow)
{
    private const int SlotCount = 5;

    public required string MenuId { get; init; }

    public required string EditorId { get; init; }

    /// <summary>The five bubbles in key order, 1 to 5.</summary>
    public required IReadOnlyList<ConnectorAction> Slots { get; init; }

    /// <summary>The right-click list: the whole catalog, so never empty.</summary>
    public required IReadOnlyList<ConnectorAction> More { get; init; }

    /// <summary>The status line, localized, in display order.</summary>
    public required IReadOnlyList<string> Status { get; init; }

    // ReSharper disable ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract -- System.Text.Json does not enforce element nullability
    internal override bool IsWellFormed =>
        Id is not null
        && Slots.Count == SlotCount
        && Slots.All(slot => slot is not null)
        && More.All(action => action is not null)
        && Status.All(line => line is not null);
    // ReSharper restore ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
}

public sealed record MenuResponseMessage() : ConnectorMessage(ConnectorMessageTypes.MenuResponse)
{
    public required bool Ok { get; init; }

    public ConnectorError? Error { get; init; }

    internal override bool IsWellFormed => Id is not null && (Ok || Error is not null);
}

public sealed record MenuChooseMessage() : ConnectorMessage(ConnectorMessageTypes.MenuChoose)
{
    public required string MenuId { get; init; }

    /// <summary>An action id from the menu, or one of <see cref="MenuChoices" />.</summary>
    public required string Action { get; init; }

    /// <summary>The user's text for <see cref="MenuChoices.Custom" />; never sent with any other action.</summary>
    public string? Instruction { get; init; }

    internal override bool IsWellFormed =>
        Action.Length > 0
        && (Action == MenuChoices.Custom ? !string.IsNullOrWhiteSpace(Instruction) : Instruction is null);
}

public sealed record MenuUpdateMessage() : ConnectorMessage(ConnectorMessageTypes.MenuUpdate)
{
    public required string MenuId { get; init; }

    /// <summary>One of <see cref="MenuStates" />.</summary>
    public required string State { get; init; }

    /// <summary>The running action's id; set with <see cref="MenuStates.Running" />.</summary>
    public string? Action { get; init; }

    /// <summary>The answer, the unapplied result, or the localized error; never traced.</summary>
    public string? Text { get; init; }

    /// <summary>Whether the error card offers Try again; set with <see cref="MenuStates.Error" />.</summary>
    public bool? CanRetry { get; init; }

    /// <summary>A finished rewrite that could not be applied, offered for Copy with <see cref="MenuStates.Error" />; never traced.</summary>
    public string? Result { get; init; }

    internal override bool IsWellFormed =>
        State switch
        {
            MenuStates.Running => Action is not null,
            MenuStates.Applied => true,
            MenuStates.Answer or MenuStates.DraftChanged => Text is not null,
            MenuStates.Error => Text is not null && CanRetry is not null,
            _ => false,
        };
}

public sealed record MenuCloseMessage() : ConnectorMessage(ConnectorMessageTypes.MenuClose)
{
    public required string MenuId { get; init; }
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
/// <param name="Markdown">The selection in the protocol's Markdown subset; sent only for a rich editor.</param>
/// <param name="FormatHash">The extension's opaque fingerprint of the selection's formatting; sent with <paramref name="Markdown" />.</param>
public sealed record CapturedSelection(string Text, int Start, int End, string? Markdown = null, string? FormatHash = null);

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
