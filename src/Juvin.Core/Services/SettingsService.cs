using System.Diagnostics;
using System.Text.Json;
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
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly AtomicJsonStore<AppSettings> _store;

    public SettingsService()
        : this(JuvinEnvironment.SettingsFilePath)
    {
    }

    public SettingsService(string filePath)
        : this(filePath, AtomicJsonBackupMode.LastKnownGood, AtomicJsonCorruptFilePolicy.PreserveAndReset)
    {
    }

    private SettingsService(string filePath, AtomicJsonBackupMode backupMode, AtomicJsonCorruptFilePolicy corruptFilePolicy)
    {
        _store = new AtomicJsonStore<AppSettings>(
            filePath,
            () => new AppSettings(),
            new AtomicJsonStoreOptions<AppSettings>
            {
                JsonOptions = s_jsonOptions,
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
        new(filePath, AtomicJsonBackupMode.None, AtomicJsonCorruptFilePolicy.Throw);

    public AppSettings Current => _store.Current;

    public event Action<AppSettings>? SettingsChanged;

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
        Update(_ => new AppSettings());
        _store.DeleteCopies();
    }
}
