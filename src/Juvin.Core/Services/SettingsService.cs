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
    {
        _store = new AtomicJsonStore<AppSettings>(
            filePath,
            () => new AppSettings(),
            new AtomicJsonStoreOptions<AppSettings>
            {
                JsonOptions = s_jsonOptions,
                BackupMode = AtomicJsonBackupMode.LastKnownGood,
                CorruptFilePolicy = AtomicJsonCorruptFilePolicy.PreserveAndReset,
                Diagnostic = d =>
                    Trace.WriteLine($"[SettingsService] {d.Kind}: {d.Path} {d.PreservedPath}"),
            }
        );
    }

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
