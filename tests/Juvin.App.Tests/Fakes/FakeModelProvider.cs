using Juvin.App.Services;
using Juvin.Core.Localization;
using Juvin.Core.Models;
using Juvin.Core.Providers;

namespace Juvin.App.Tests.Fakes;

/// <summary>A provider whose probe answers from <see cref="FakeProviders" /> without touching the network.</summary>
internal sealed class FakeModelProvider : IModelProvider
{
    private readonly Task? _gate;
    private readonly ProviderStatus _status;

    public FakeModelProvider(ProcessingMode mode, ProviderStatus status, Task? gate = null)
    {
        Mode = mode;
        _status = status;
        _gate = gate;
    }

    public ProcessingMode Mode { get; }

    public string DisplayName => "Fake " + Mode;

    public bool SupportsStreaming => true;

    public bool RunsLocally => Mode == ProcessingMode.Local;

    public async Task<ProviderStatus> ProbeAsync(CancellationToken ct)
    {
        if (_gate is not null)
        {
            await _gate.WaitAsync(ct);
        }

        return _status;
    }

    public IAsyncEnumerable<string> StreamAsync(ChatRequest request, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<ChatResult> CompleteAsync(ChatRequest request, IProgress<string>? preview, CancellationToken ct) =>
        throw new NotSupportedException();
}

/// <summary>Builds a <see cref="ModelProbeService" /> over fake providers keyed by server URL.</summary>
internal sealed class FakeProviders
{
    private static readonly ProviderStatus s_unreachable =
        new(ProviderStatusKind.Unreachable, "Provider.Status.Unreachable", []);

    public Dictionary<string, ProviderStatus> Servers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ProviderStatus Codex { get; set; } = new(ProviderStatusKind.NotInstalled, "Provider.Status.NotInstalled", []);

    /// <summary>When set, every local probe waits for it, so a test can act while a check is running.</summary>
    public TaskCompletionSource? LocalProbeGate { get; set; }

    public static ProviderStatus Reachable(string? version, params ModelInfo[] models) =>
        new(ProviderStatusKind.NoModel, "Provider.Status.NoModel", models, version);

    public static ModelInfo Model(string id, string? size = null, bool lowQuality = false) =>
        new(id, id, size, lowQuality);

    public ModelProbeService CreateService(Localizer localizer) =>
        new(
            new ProviderSelector(
                settings => new FakeModelProvider(
                    ProcessingMode.Local,
                    Servers.GetValueOrDefault(settings.LocalServerUrl, s_unreachable),
                    LocalProbeGate?.Task
                ),
                _ => new FakeModelProvider(ProcessingMode.Codex, Codex)
            ),
            new HttpClient(),
            localizer
        );
}
