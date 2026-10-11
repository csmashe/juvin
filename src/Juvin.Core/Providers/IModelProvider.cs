using Juvin.Core.Models;

namespace Juvin.Core.Providers;

public interface IModelProvider
{
    ProcessingMode Mode { get; }

    string DisplayName { get; }

    bool SupportsStreaming { get; }

    /// <summary>True only when requests never leave this machine.</summary>
    bool RunsLocally { get; }

    Task<ProviderStatus> ProbeAsync(CancellationToken ct);

    /// <summary>Yields visible-text deltas only; a non-streaming provider yields once.</summary>
    // ReSharper disable once UnusedMemberInSuper.Global -- the panel's live preview consumes it; the session uses CompleteAsync
    IAsyncEnumerable<string> StreamAsync(ChatRequest request, CancellationToken ct);

    /// <summary>
    ///     Runs one request and returns the whole reply. <paramref name="preview" /> receives the
    ///     accumulated text as it grows (once, for a provider that does not stream).
    /// </summary>
    Task<ChatResult> CompleteAsync(
        ChatRequest request,
        IProgress<string>? preview,
        CancellationToken ct
    );
}
