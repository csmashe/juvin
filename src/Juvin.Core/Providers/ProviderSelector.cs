using Juvin.Core.Models;

namespace Juvin.Core.Providers;

/// <summary>
///     Picks the provider for the configured processing mode. There is deliberately no fallback:
///     a failure in one mode is reported, never retried through the other.
/// </summary>
public sealed class ProviderSelector
{
    private readonly Func<AppSettings, IModelProvider> _codex;
    private readonly Func<AppSettings, IModelProvider> _local;

    public ProviderSelector(
        Func<AppSettings, IModelProvider> local,
        Func<AppSettings, IModelProvider> codex
    )
    {
        _local = local;
        _codex = codex;
    }

    /// <param name="http">The client for local model servers.</param>
    /// <param name="runner">Runs the Codex CLI.</param>
    /// <param name="codexSearchDirectories">Where to look for the Codex CLI after PATH.</param>
    public static ProviderSelector CreateDefault(HttpClient http, IProcessRunner runner, IReadOnlyList<string>? codexSearchDirectories = null) =>
        new(
            settings => new OpenAiCompatibleProvider(http, settings),
            settings => new CodexCliProvider(settings, runner, codexSearchDirectories ?? [])
        );

    /// <exception cref="ProviderException">The provider for the mode is misconfigured.</exception>
    public IModelProvider Select(AppSettings settings)
    {
        var provider = settings.ProcessingMode switch
        {
            ProcessingMode.Local => _local(settings),
            ProcessingMode.Codex => _codex(settings),
            _ => throw new ProviderException(
                ProviderFailureKind.Configuration,
                $"The processing mode {(int)settings.ProcessingMode} is not one Juvin knows. Choose a mode in Settings."
            ),
        };

        return provider.Mode == settings.ProcessingMode
            ? provider
            : throw new InvalidOperationException(
                $"The {settings.ProcessingMode} factory returned a {provider.Mode} provider."
            );
    }
}
