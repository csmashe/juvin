using System.Diagnostics;
using Juvin.Core.Localization;
using Juvin.Core.Models;
using Juvin.Core.Providers;

namespace Juvin.App.Services;

/// <summary>
///     Probes the local server and the Codex CLI for the Models section and the setup wizard, and
///     turns the results into the text both show.
/// </summary>
public sealed class ModelProbeService
{
    public const string OllamaUrl = "http://localhost:11434";
    public const string LmStudioUrl = "http://localhost:1234";
    public const string OllamaPullCommand = "ollama pull qwen3.5:27b";

    private readonly Localizer _localizer;
    private readonly ProviderSelector _providers;

    public ModelProbeService(ProviderSelector providers, Localizer localizer)
    {
        _providers = providers;
        _localizer = localizer;
    }

    public static bool IsReachable(ProviderStatus status) =>
        status.Kind is ProviderStatusKind.Ready or ProviderStatusKind.NoModel;

    /// <summary>Only Ollama reports a version, so a version means the server is Ollama.</summary>
    public static bool IsOllama(ProviderStatus status) => status.Version is not null;

    /// <summary>Chat models that run on the server itself; cloud-forwarded and embedding-only models are never offered.</summary>
    public static IReadOnlyList<ModelInfo> UsableModels(IReadOnlyList<ModelInfo> models) =>
        [.. models.Where(model => model is { IsRemote: false, SupportsChat: true })];

    /// <summary>The first usable model that is not low-quality class, else the first usable model, else null.</summary>
    public static ModelInfo? RecommendModel(IReadOnlyList<ModelInfo> models)
    {
        var usable = UsableModels(models);
        return usable.FirstOrDefault(model => !model.IsLowQualityClass) ?? (usable.Count > 0 ? usable[0] : null);
    }

    /// <summary>The readiness a probe would report if <paramref name="modelId" /> were the selected model.</summary>
    public static ProviderStatus WithSelectedModel(ProviderStatus status, string? modelId)
    {
        if (!IsReachable(status))
        {
            return status;
        }

        var selected = status.Models.FirstOrDefault(model => IsSameModel(model.Id, modelId));
        return selected switch
        {
            null => status with { Kind = ProviderStatusKind.NoModel, MessageKey = "Provider.Status.NoModel" },
            { IsRemote: true } => status with { Kind = ProviderStatusKind.NoModel, MessageKey = "Provider.Status.RemoteModel" },
            { SupportsChat: false } => status with { Kind = ProviderStatusKind.NoModel, MessageKey = "Provider.Status.NotAChatModel" },
            _ => status with { Kind = ProviderStatusKind.Ready, MessageKey = "Provider.Status.Ready" },
        };
    }

    public static bool IsSameModel(string id, string? configured) =>
        configured is not null
        && (
            string.Equals(id, configured, StringComparison.OrdinalIgnoreCase)
            || string.Equals(id, configured + ":latest", StringComparison.OrdinalIgnoreCase)
        );

    public static bool IsSameServer(string a, string b) =>
        string.Equals(a.Trim().TrimEnd('/'), b.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>True for a loopback address, false for any other host, null when the URL is not valid.</summary>
    public static bool? IsThisDevice(string url) => OpenAiCompatibleProvider.IsLoopbackServer(url);

    public string LocationLabel(string url) =>
        IsThisDevice(url) switch
        {
            true => _localizer["Models.ThisDevice"],
            false => _localizer["Models.PrivateServer"],
            null => "",
        };

    public async Task<ProviderStatus> ProbeLocalAsync(AppSettings settings, string url, CancellationToken ct)
    {
        IModelProvider provider;
        try
        {
            provider = LocalProvider(settings, url);
        }
        catch (ProviderException)
        {
            return new ProviderStatus(ProviderStatusKind.Unreachable, "Models.Status.InvalidUrl", []);
        }

        return await ProbeAsync(provider, ct);
    }

    public Task<ProviderStatus> ProbeCodexAsync(AppSettings settings, CancellationToken ct) =>
        ProbeAsync(_providers.Select(settings with { ProcessingMode = ProcessingMode.Codex }), ct);

    /// <summary>"Ollama 0.12.3" when the server says so, otherwise <paramref name="fallbackName" />.</summary>
    public string ServerName(ProviderStatus status, string? fallbackName = null) =>
        status.Version is { } version
            ? $"Ollama {version}"
            : fallbackName ?? _localizer["Models.OpenAiServer"];

    public string ModelCount(int count) =>
        count == 1 ? _localizer["Models.ModelCountOne"] : _localizer.Format("Models.ModelCount", count);

    public string DescribeLocal(ProviderStatus status)
    {
        if (!IsReachable(status))
        {
            return _localizer[status.MessageKey];
        }

        var connected = _localizer.Format("Models.Status.Connected", ServerName(status), ModelCount(status.Models.Count));
        return status.Kind == ProviderStatusKind.Ready ? connected : $"{connected} {_localizer[status.MessageKey]}";
    }

    private string CodexName(ProviderStatus status)
    {
        // Codex prints "codex-cli 0.162.0"; the last word is the version.
        var version = status.Version?.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return version is null ? _localizer["Models.Codex"] : _localizer.Format("Models.CodexVersion", version);
    }

    public string DescribeCodex(ProviderStatus status) =>
        status.Kind == ProviderStatusKind.Ready
            ? _localizer.Format("Models.Status.CodexReady", CodexName(status))
            : _localizer[status.MessageKey];

    private IModelProvider LocalProvider(AppSettings settings, string url) =>
        _providers.Select(settings with { ProcessingMode = ProcessingMode.Local, LocalServerUrl = url });

    private static async Task<ProviderStatus> ProbeAsync(IModelProvider provider, CancellationToken ct)
    {
        try
        {
            return await provider.ProbeAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Trace.WriteLine($"[ModelProbeService] {provider.Mode} probe failed: {ex.GetType().Name}");
            return new ProviderStatus(ProviderStatusKind.Unreachable, "Models.Status.CheckFailed", []);
        }
    }
}
