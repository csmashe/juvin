using Juvin.Core.Models;

namespace Juvin.Core.Interfaces;

/// <summary>
///     The opt-in rewrite history. Mutations save immediately and throw <see cref="IOException" /> or
///     <see cref="UnauthorizedAccessException" /> when the file cannot be written.
/// </summary>
public interface IHistoryService
{
    /// <summary>Newest first; empty when the file could not be read.</summary>
    IReadOnlyList<HistoryEntry> Entries { get; }

    /// <summary>False when the history file exists but could not be read.</summary>
    bool IsAvailable { get; }

    /// <summary>Raised after a mutation changed the saved entries.</summary>
    event Action? Changed;

    void Add(HistoryEntry entry);

    void MarkApplied(string id);

    void Delete(string id);

    void Clear();

    /// <summary>Removes entries older than <paramref name="retentionDays" />; zero or less keeps everything.</summary>
    void Prune(int retentionDays, DateTimeOffset now);
}
