using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Juvin.Core.Localization;

/// <summary>
///     String table backed by flat JSON catalogs (<c>{"Section.Key": "text"}</c>), one file per
///     language named <c>&lt;code&gt;.json</c>. Lookups fall back from the current language to
///     English and then to the key itself, so a missing string is visible but never fatal.
/// </summary>
public sealed class Localizer
{
    private const string FallbackLanguage = "en";

    private readonly string _catalogDirectory;
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> _catalogs =
        new(StringComparer.OrdinalIgnoreCase);

    public Localizer()
        : this(DefaultCatalogDirectory)
    {
    }

    public Localizer(string catalogDirectory)
    {
        _catalogDirectory = catalogDirectory;
    }

    public static string DefaultCatalogDirectory =>
        Path.Join(AppContext.BaseDirectory, "Resources", "Localization");

    public string CurrentLanguage { get; private set; } = FallbackLanguage;

    public event Action<string>? LanguageChanged;

    public string this[string key] =>
        Lookup(CurrentLanguage, key) ?? Lookup(FallbackLanguage, key) ?? key;

    public string Format(string key, params object?[] args)
    {
        return string.Format(CultureInfo.CurrentCulture, this[key], args);
    }

    // ReSharper disable once MemberCanBePrivate.Global -- feeds the language picker in the settings UI
    public IReadOnlyList<string> AvailableLanguages()
    {
        if (!Directory.Exists(_catalogDirectory))
        {
            return [];
        }

        return
        [
            .. Directory
                .EnumerateFiles(_catalogDirectory, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .OfType<string>()
                .Order(StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>
    ///     Maps a language setting to a language that has a catalog. Null or empty means follow
    ///     the OS UI language. Anything without a catalog resolves to English.
    /// </summary>
    public string ResolveLanguage(string? setting)
    {
        var requested = string.IsNullOrWhiteSpace(setting)
            ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
            : setting.Trim();
        var available = AvailableLanguages();

        return available.FirstOrDefault(language =>
                string.Equals(language, requested, StringComparison.OrdinalIgnoreCase)
            )
            ?? available.FirstOrDefault(language =>
                string.Equals(
                    language,
                    requested.Split('-', '_')[0],
                    StringComparison.OrdinalIgnoreCase
                )
            )
            ?? FallbackLanguage;
    }

    public void SetLanguage(string? setting)
    {
        var language = ResolveLanguage(setting);
        if (string.Equals(language, CurrentLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        CurrentLanguage = language;
        LanguageChanged?.Invoke(language);
    }

    private string? Lookup(string language, string key)
    {
        return _catalogs.GetOrAdd(language, LoadCatalog).GetValueOrDefault(key);
    }

    private IReadOnlyDictionary<string, string> LoadCatalog(string language)
    {
        var path = Path.Join(_catalogDirectory, language + ".json");
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
                ?? new Dictionary<string, string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Trace.WriteLine($"[Localizer] Could not load catalog {path}: {ex.Message}");
            return new Dictionary<string, string>();
        }
    }
}
