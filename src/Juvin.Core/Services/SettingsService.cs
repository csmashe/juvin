using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Juvin.Core.Interfaces;
using Juvin.Core.Models;

namespace Juvin.Core.Services;

public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    private readonly AppSettings _defaults;
    private readonly AtomicJsonStore<AppSettings> _store;

    public SettingsService(string filePath)
        : this(filePath, new AppSettings())
    {
    }

    /// <param name="filePath">The settings file.</param>
    /// <param name="defaults">The settings a missing, reset, or unreadable file starts from.</param>
    public SettingsService(string filePath, AppSettings defaults)
        : this(filePath, defaults, AtomicJsonBackupMode.LastKnownGood, AtomicJsonCorruptFilePolicy.PreserveAndReset)
    {
    }

    private SettingsService(
        string filePath,
        AppSettings defaults,
        AtomicJsonBackupMode backupMode,
        AtomicJsonCorruptFilePolicy corruptFilePolicy
    )
    {
        _defaults = defaults;
        _store = new AtomicJsonStore<AppSettings>(
            filePath,
            () => _defaults,
            new AtomicJsonStoreOptions<AppSettings>
            {
                JsonOptions = s_jsonOptions,
                Deserialize = OverDefaults,
                BackupMode = backupMode,
                CorruptFilePolicy = corruptFilePolicy,
                Diagnostic = d =>
                    Trace.WriteLine($"[SettingsService] {d.Kind}: {d.Path} {d.PreservedPath}"),
            }
        );
    }

    /// <summary>
    ///     Reads the settings for a caller that does not own them, such as the CLI. Nothing on disk
    ///     is touched: the backup is not consulted, and a corrupt file is left in place with its
    ///     <see cref="JsonException" /> surfacing from <see cref="Current" />.
    /// </summary>
    public static SettingsService OpenForReading(string filePath) =>
        new(filePath, new AppSettings(), AtomicJsonBackupMode.None, AtomicJsonCorruptFilePolicy.Throw);

    public AppSettings Current => _store.Current;

    // A property the file lacks takes the per-OS default rather than the record's initializer.
    private AppSettings OverDefaults(string json)
    {
        if (JsonSerializer.Deserialize<JsonNode>(json, s_jsonOptions) is not JsonObject saved)
        {
            throw new JsonException("The settings file does not hold a JSON object.");
        }

        var merged = JsonSerializer.SerializeToNode(_defaults, s_jsonOptions)!.AsObject();
        foreach (var (name, value) in saved)
        {
            merged[name] = value?.DeepClone();
        }

        return merged.Deserialize<AppSettings>(s_jsonOptions)!;
    }

    public event Action<AppSettings>? SettingsChanged;

    public event Action? SettingsReset;

    public void Load()
    {
        _store.Reload();
    }

    public void Update(Func<AppSettings, AppSettings> update)
    {
        var next = _store.Update(update, out var changed);
        if (changed)
        {
            SettingsChanged?.Invoke(next);
        }
    }

    public void Reset()
    {
        Update(_ => _defaults);
        try
        {
            _store.DeleteCopies();
        }
        finally
        {
            SettingsReset?.Invoke();
        }
    }
}
