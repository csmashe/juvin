using System.Diagnostics;
using Juvin.Platform;

namespace Juvin.App.Services;

/// <summary>The last answer from the tray host probe; an unknown answer keeps the previous one.</summary>
internal sealed class TrayHostAvailability(ITrayHostProbe probe)
{
    public bool IsAvailable { get; private set; }

    /// <summary>Raised on the thread that called <see cref="RefreshAsync" /> when <see cref="IsAvailable" /> changes.</summary>
    public event EventHandler? Changed;

    /// <summary>The startup probe; an unknown answer counts as no host.</summary>
    public void ProbeBlocking()
    {
        // Startup decides whether a login start stays hidden, so this one probe blocks; the probe is bounded.
        IsAvailable = Task.Run(() => probe.IsTrayHostAvailableAsync(CancellationToken.None)).GetAwaiter().GetResult() ?? false;
    }

    public async Task<bool> RefreshAsync()
    {
        if (await probe.IsTrayHostAvailableAsync(CancellationToken.None) is not { } answer || answer == IsAvailable)
        {
            return IsAvailable;
        }

        IsAvailable = answer;
        Trace.WriteLine($"[TrayHostAvailability] Tray available: {IsAvailable}");
        Changed?.Invoke(this, EventArgs.Empty);
        return IsAvailable;
    }
}
