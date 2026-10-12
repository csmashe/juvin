using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Juvin.Core.Interfaces;
using Juvin.Core.Models;

namespace Juvin.Core.Services;

public sealed class HistoryService : IHistoryService
{
    internal static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        Converters = { new JsonStringEnumConverter() },
    };

    private const int MaxRetentionDays = 36500;

    private readonly string _filePath;
    private readonly AtomicJsonStore<ImmutableArray<HistoryEntry>> _store;

    public HistoryService()
        : this(JuvinEnvironment.HistoryFilePath)
    {
    }

    public HistoryService(string filePath)
    {
        _filePath = Path.GetFullPath(filePath);
        // No backup copy: a deleted entry must not survive in the previous snapshot.
        _store = new AtomicJsonStore<ImmutableArray<HistoryEntry>>(
            _filePath,
            () => [],
            new AtomicJsonStoreOptions<ImmutableArray<HistoryEntry>>
            {
                JsonOptions = JsonOptions,
                Deserialize = Deserialize,
                CorruptFilePolicy = AtomicJsonCorruptFilePolicy.PreserveAndReset,
                Diagnostic = d => Trace.WriteLine($"[HistoryService] {d.Kind}: {d.Path} {d.PreservedPath}"),
            }
        );
    }

    public IReadOnlyList<HistoryEntry> Entries => TryRead(out var entries) ? entries : [];

    public bool IsAvailable => TryRead(out _);

    public event Action? Changed;

    public void Add(HistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Update(entries => entries.Insert(0, entry));
    }

    public void MarkApplied(string id) =>
        Update(entries => [.. entries.Select(entry => entry.Id == id ? entry with { Applied = true } : entry)]);

    public void Delete(string id) => Update(entries => entries.RemoveAll(entry => entry.Id == id));

    /// <summary>Also removes preserved copies of a corrupt history file, which hold email content too.</summary>
    public void Clear()
    {
        Update(_ => []);
        _store.DeleteCopies();
    }

    public void Prune(int retentionDays, DateTimeOffset now)
    {
        if (retentionDays <= 0)
        {
            return;
        }

        // Capped so a hand-edited period cannot overflow the subtraction and block startup.
        var cutoff = now - TimeSpan.FromDays(Math.Min(retentionDays, MaxRetentionDays));
        // Runs even with nothing to drop: publishing heals a corrupt primary, whose content would
        // otherwise be preserved again at every load. A missing file is left missing.
        if (File.Exists(_filePath))
        {
            Update(entries => entries.RemoveAll(entry => entry.CreatedAt < cutoff));
        }

        _store.DeleteCopies(cutoff.UtcDateTime);
    }

    /// <summary>A null entry or text would crash every reader, so it counts as corruption and is set aside.</summary>
    private static ImmutableArray<HistoryEntry> Deserialize(string json)
    {
        var entries = JsonSerializer.Deserialize<ImmutableArray<HistoryEntry>>(json, JsonOptions);
        if (entries.IsDefault || !entries.All(IsComplete))
        {
            throw new JsonException("The history file holds a null entry or a null text.");
        }

        return entries;
    }

    // ReSharper disable RedundantAlwaysMatchSubpattern -- the history file can hold null where the model says it cannot
    private static bool IsComplete(HistoryEntry? entry) =>
        entry is { Id: not null, ActionId: not null, ActionTitle: not null, Adapter: not null, Draft: not null, Result: not null, ProviderName: not null, ModelId: not null }
        && (entry.MessagesSent ?? []).All(message => message is { Content: not null });
    // ReSharper restore RedundantAlwaysMatchSubpattern

    private void Update(Func<ImmutableArray<HistoryEntry>, ImmutableArray<HistoryEntry>> update)
    {
        _store.Update(update, out var changed);
        if (changed)
        {
            Changed?.Invoke();
        }
    }

    private bool TryRead(out ImmutableArray<HistoryEntry> entries)
    {
        try
        {
            entries = _store.Current;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"[HistoryService] Could not read history: {ex.GetType().Name}");
            entries = [];
            return false;
        }
    }
}
