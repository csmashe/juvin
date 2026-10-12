using Juvin.Core.Interfaces;
using Juvin.Core.Models;

namespace Juvin.App.Tests.Fakes;

public sealed class FakeHistoryService : IHistoryService
{
    private readonly List<HistoryEntry> _entries = [];

    /// <summary>Thrown by every mutation while set.</summary>
    public Exception? Failure { get; set; }

    public IReadOnlyList<HistoryEntry> Entries => _entries;

    public bool IsAvailable { get; set; } = true;

    public List<(int RetentionDays, DateTimeOffset Now)> Prunes { get; } = [];

    public event Action? Changed;

    public void Add(HistoryEntry entry) => Mutate(() => _entries.Insert(0, entry));

    public void MarkApplied(string id) =>
        Mutate(() =>
        {
            var index = _entries.FindIndex(entry => entry.Id == id);
            if (index >= 0)
            {
                _entries[index] = _entries[index] with { Applied = true };
            }
        });

    public void Delete(string id) => Mutate(() => _entries.RemoveAll(entry => entry.Id == id));

    public void Clear() => Mutate(_entries.Clear);

    public void Prune(int retentionDays, DateTimeOffset now) =>
        Mutate(() =>
        {
            Prunes.Add((retentionDays, now));
            if (retentionDays > 0)
            {
                _entries.RemoveAll(entry => entry.CreatedAt < now - TimeSpan.FromDays(retentionDays));
            }
        });

    private void Mutate(Action mutation)
    {
        if (Failure is not null)
        {
            throw Failure;
        }

        mutation();
        Changed?.Invoke();
    }
}
