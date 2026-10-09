namespace Juvin.Core.Providers;

public enum ChatRole
{
    User,
    Assistant,
}

public sealed record ChatMessage(ChatRole Role, string Content);

public sealed record ChatRequest
{
    public required string SystemPrompt { get; init; }

    public required IReadOnlyList<ChatMessage> Messages { get; init; }

    /// <summary>Null means the model configured for the provider.</summary>
    public string? Model { get; init; }

    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global -- a per-request override for callers that need it
    public double Temperature { get; init; } = 0.3;

    public bool DisableThinking { get; init; }
}

public sealed record ChatResult(string Text, string Model, int? InputTokens, int? OutputTokens);

public enum ProviderStatusKind
{
    Ready,
    Unreachable,
    NotSignedIn,
    NotInstalled,
    NoModel,

    /// <summary>Installed and signed in, but too old for the flags Juvin needs.</summary>
    Incompatible,
}

/// <param name="IsRemote">The server forwards this model to a cloud service, so it is not local.</param>
/// <param name="SupportsChat">False for a model that only embeds and cannot generate text.</param>
public sealed record ModelInfo(
    string Id,
    string DisplayName,
    string? ParameterSize,
    bool IsLowQualityClass,
    bool IsRemote = false,
    bool SupportsChat = true
);

/// <param name="MessageKey">String table key describing the status to the user.</param>
/// <param name="Version">The server or CLI version, when the provider reports one.</param>
public sealed record ProviderStatus(
    ProviderStatusKind Kind,
    string MessageKey,
    IReadOnlyList<ModelInfo> Models,
    string? Version = null
);
