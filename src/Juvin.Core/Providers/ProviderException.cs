namespace Juvin.Core.Providers;

public enum ProviderFailureKind
{
    Configuration,
    Unreachable,
    Protocol,
    ProcessFailed,
    Incompatible,
    Cancelled,
}

/// <summary>
///     A provider failure with a message safe to show the user. The message never carries the
///     prompt or the response.
/// </summary>
public sealed class ProviderException : Exception
{
    public ProviderException(
        ProviderFailureKind kind,
        string message,
        Exception? innerException = null
    )
        : base(message, innerException)
    {
        Kind = kind;
    }

    public ProviderFailureKind Kind { get; }
}
