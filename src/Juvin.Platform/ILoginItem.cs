namespace Juvin.Platform;

/// <summary>
///     The OS entry that starts the app when the user signs in. The entry passes
///     <see cref="BackgroundArgument" /> so the app starts without showing its window.
/// </summary>
public interface ILoginItem
{
    const string BackgroundArgument = "--background";

    /// <summary>False only where the OS has no supported way to start the app at login.</summary>
    bool IsSupported { get; }

    /// <summary>Writes or refreshes the entry. Throws <see cref="LoginItemException" /> when the OS refuses.</summary>
    Task EnableAsync(CancellationToken ct);

    /// <summary>Removes the entry if it is the app's own. Throws <see cref="LoginItemException" /> when the OS refuses.</summary>
    Task DisableAsync(CancellationToken ct);
}

public sealed class LoginItemException(string message, Exception? inner = null) : Exception(message, inner);
