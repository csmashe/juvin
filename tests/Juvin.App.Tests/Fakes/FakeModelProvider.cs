using Juvin.App.Services;
using Juvin.Core.Localization;
using Juvin.Core.Models;
using Juvin.Core.Providers;

namespace Juvin.App.Tests.Fakes;

/// <summary>
///     A provider whose probe answers from <see cref="FakeProviders" /> and whose completions
///     return <see cref="Reply" />, without touching the network.
/// </summary>
internal sealed class FakeModelProvider : IModelProvider
{
    private readonly Task? _gate;
    private readonly ProviderStatus _status;

    public FakeModelProvider(ProcessingMode mode, ProviderStatus status, Task? gate = null)
    {
        Mode = mode;
        RunsLocally = mode == ProcessingMode.Local;
        _status = status;
        _gate = gate;
    }

    public FakeModelProvider(ProcessingMode mode)
        : this(mode, new ProviderStatus(ProviderStatusKind.Ready, "Provider.Status.Ready", []))
    {
    }

    public ProcessingMode Mode { get; }

    public string DisplayName => "Fake " + Mode;

    public bool SupportsStreaming { get; init; } = true;

    public bool RunsLocally { get; }

    /// <summary>Every request received, oldest first.</summary>
    public List<ChatRequest> Requests { get; } = [];

    public Func<ChatRequest, string> Reply { get; set; } = request => "reply " + request.Messages.Count;

    /// <summary>Thrown after the first half of the reply has been previewed.</summary>
    public Exception? Failure { get; set; }

    /// <summary>When set, a completion previews half its reply and then waits for this before finishing.</summary>
    public TaskCompletionSource? CompletionGate { get; set; }

    /// <summary>When set, reported in order in place of the half-reply preview.</summary>
    public IReadOnlyList<string>? Previews { get; init; }

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

    public async Task<ChatResult> CompleteAsync(ChatRequest request, IProgress<string>? preview, CancellationToken ct)
    {
        Requests.Add(request);
        var text = Reply(request);
        foreach (var step in Previews ?? [text[..(text.Length / 2)]])
        {
            preview?.Report(step);
        }

        if (CompletionGate is not null)
        {
            await CompletionGate.Task.WaitAsync(ct);
        }

        if (Failure is not null)
        {
            throw Failure;
        }

        preview?.Report(text);
        return new ChatResult(text, request.Model ?? "fake-model", 100, 20);
    }
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
            localizer
        );
}
