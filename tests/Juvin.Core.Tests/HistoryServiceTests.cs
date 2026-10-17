using Juvin.Core.Models;
using Juvin.Core.Services;

namespace Juvin.Core.Tests;

public sealed class HistoryServiceTests : IDisposable
{
    private static readonly DateTimeOffset s_now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        _temp.Dispose();
    }

    [Fact]
    public void Entries_AreEmptyWhenNoFileExists()
    {
        var service = new HistoryService(_temp.File("history.json"));

        Assert.Empty(service.Entries);
        Assert.True(service.IsAvailable);
    }

    [Fact]
    public void Add_PutsTheNewestFirstAndPersists()
    {
        var path = _temp.File("history.json");
        var service = new HistoryService(path);
        var changes = 0;
        service.Changed += () => changes++;

        service.Add(Entry("a", s_now.AddMinutes(-1)));
        service.Add(Entry("b", s_now));

        Assert.Equal(["b", "a"], service.Entries.Select(e => e.Id));
        Assert.Equal(2, changes);
        var reloaded = new HistoryService(path).Entries;
        Assert.Equal(["b", "a"], reloaded.Select(e => e.Id));
        Assert.Equal(Entry("a", s_now.AddMinutes(-1)), reloaded[1]);
        Assert.False(File.Exists(path + ".bak"));
    }

    [Fact]
    public void MarkApplied_SetsOnlyThatEntry()
    {
        var service = new HistoryService(_temp.File("history.json"));
        service.Add(Entry("a", s_now));
        service.Add(Entry("b", s_now));

        service.MarkApplied("a");

        Assert.False(service.Entries[0].Applied);
        Assert.True(service.Entries[1].Applied);
    }

    [Fact]
    public void Delete_RemovesOneEntry()
    {
        var service = new HistoryService(_temp.File("history.json"));
        service.Add(Entry("a", s_now));
        service.Add(Entry("b", s_now));

        service.Delete("b");

        Assert.Equal(["a"], service.Entries.Select(e => e.Id));
    }

    [Fact]
    public void Clear_RemovesEverythingAndPreservedCopies()
    {
        var path = _temp.File("history.json");
        var service = new HistoryService(path);
        service.Add(Entry("a", s_now));
        File.WriteAllText(path + ".broken-1", "draft text");
        var staging = _temp.File(".history.json.abc.tmp");
        File.WriteAllText(staging, "draft text");

        service.Clear();

        Assert.Empty(service.Entries);
        Assert.Empty(new HistoryService(path).Entries);
        Assert.False(File.Exists(path + ".broken-1"));
        Assert.False(File.Exists(staging));
    }

    [Fact]
    public void Prune_DropsEntriesOlderThanTheRetention()
    {
        var service = new HistoryService(_temp.File("history.json"));
        service.Add(Entry("old", s_now.AddDays(-31)));
        service.Add(Entry("new", s_now.AddDays(-29)));
        var changes = 0;
        service.Changed += () => changes++;

        service.Prune(30, s_now);
        service.Prune(30, s_now);

        Assert.Equal(["new"], service.Entries.Select(e => e.Id));
        Assert.Equal(1, changes);
    }

    [Fact]
    public void Prune_WithAnOversizedRetentionKeepsEverything()
    {
        var service = new HistoryService(_temp.File("history.json"));
        service.Add(Entry("a", s_now.AddDays(-400)));

        service.Prune(int.MaxValue, s_now);

        Assert.Single(service.Entries);
    }

    [Fact]
    public void Prune_WithZeroKeepsEverything()
    {
        var path = _temp.File("history.json");
        var service = new HistoryService(path);
        service.Add(Entry("old", s_now.AddYears(-5)));
        var written = File.GetLastWriteTimeUtc(path);

        service.Prune(0, s_now);

        Assert.Single(service.Entries);
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void ACorruptFile_IsPreservedAndReset()
    {
        var path = _temp.File("history.json");
        File.WriteAllText(path, "[{ not json");
        var service = new HistoryService(path);

        Assert.Empty(service.Entries);
        Assert.True(service.IsAvailable);
        var preserved = Assert.Single(Directory.GetFiles(_temp.Path, "history.json.broken-*"));
        Assert.Equal("[{ not json", File.ReadAllText(preserved));

        service.Add(Entry("a", s_now));
        Assert.Single(new HistoryService(path).Entries);
    }

    [Fact]
    public void AnUnreadableFile_IsUnavailableAndEmpty()
    {
        // A directory where the file should be cannot be read on any OS.
        var path = _temp.File("history.json");
        Directory.CreateDirectory(path);
        var service = new HistoryService(path);

        Assert.Empty(service.Entries);
        Assert.False(service.IsAvailable);
        var ex = Assert.ThrowsAny<Exception>(() => service.Add(Entry("a", s_now)));
        Assert.True(ex is IOException or UnauthorizedAccessException, ex.GetType().Name);
    }

    private static HistoryEntry Entry(string id, DateTimeOffset createdAt) =>
        new()
        {
            Id = id,
            CreatedAt = createdAt,
            ActionId = "clearer",
            ActionTitle = "Make clearer",
            Subject = "Re: timeline",
            Adapter = "gmail",
            Draft = "draft",
            Result = "result",
            Mode = ProcessingMode.Local,
            ProviderName = "Local model",
            ModelId = "qwen3",
            RanLocally = true,
            ThreadMessageCount = 2,
            Completeness = Rewrite.ThreadCompleteness.LoadedMessagesOnly,
            Duration = TimeSpan.FromMilliseconds(1234),
        };

    [Fact]
    public void ANullEntry_CountsAsCorruptionInsteadOfCrashingReaders()
    {
        var path = _temp.File("history.json");
        File.WriteAllText(path, "[null]");
        var service = new HistoryService(path);

        Assert.Empty(service.Entries);
        Assert.True(service.IsAvailable);
        service.Prune(30, s_now);
        Assert.Single(Directory.GetFiles(_temp.Path, "history.json.broken-*"));
    }

    [Theory]
    [InlineData("[{\"id\":\"a\",\"draft\":null}]")]
    [InlineData("[{\"id\":\"a\",\"messagesSent\":[null]}]")]
    [InlineData("[{\"id\":\"a\",\"messagesSent\":[{\"role\":\"User\",\"content\":null}]}]")]
    public void ANullText_CountsAsCorruption(string json)
    {
        var path = _temp.File("history.json");
        File.WriteAllText(path, json);

        var service = new HistoryService(path);

        Assert.Empty(service.Entries);
        Assert.Single(Directory.GetFiles(_temp.Path, "history.json.broken-*"));
    }

    [Fact]
    public void Prune_RemovesPreservedCopiesOlderThanTheRetention()
    {
        var path = _temp.File("history.json");
        var service = new HistoryService(path);
        var old = path + ".broken-old";
        var recent = path + ".broken-recent";
        File.WriteAllText(old, "draft text");
        File.SetLastWriteTimeUtc(old, s_now.AddDays(-31).UtcDateTime);
        File.WriteAllText(recent, "draft text");
        File.SetLastWriteTimeUtc(recent, s_now.AddDays(-1).UtcDateTime);

        service.Prune(30, s_now);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
    }

    [Fact]
    public void Prune_HealsACorruptFileSoItIsNotPreservedAgain()
    {
        var path = _temp.File("history.json");
        File.WriteAllText(path, "[{ not json");
        var service = new HistoryService(path);

        service.Prune(30, s_now);
        foreach (var copy in Directory.GetFiles(_temp.Path, "history.json.broken-*"))
        {
            File.Delete(copy);
        }

        Assert.Equal("[]", File.ReadAllText(path));
        Assert.Empty(new HistoryService(path).Entries);
        Assert.Empty(Directory.GetFiles(_temp.Path, "history.json.broken-*"));
    }

    [Fact]
    public void MarkApplied_WithAnUnknownIdDoesNotWrite()
    {
        var path = _temp.File("history.json");
        var service = new HistoryService(path);
        service.Add(Entry("a", s_now));
        var written = File.GetLastWriteTimeUtc(path);
        var content = File.ReadAllText(path);
        var changes = 0;
        service.Changed += () => changes++;

        service.MarkApplied("missing");

        Assert.Equal(0, changes);
        Assert.Equal(content, File.ReadAllText(path));
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void Delete_KeepsPreservedCopiesForClear()
    {
        var path = _temp.File("history.json");
        var service = new HistoryService(path);
        service.Add(Entry("a", s_now));
        File.WriteAllText(path + ".broken-1", "draft text");

        service.Delete("a");

        Assert.Empty(service.Entries);
        Assert.True(File.Exists(path + ".broken-1"));
    }

    [Fact]
    public void Prune_RemovesTheCopyOfAnExpiredCorruptFile()
    {
        var path = _temp.File("history.json");
        File.WriteAllText(path, "[{ not json");
        File.SetLastWriteTimeUtc(path, s_now.AddDays(-90).UtcDateTime);
        var service = new HistoryService(path);

        service.Prune(30, s_now);

        Assert.Empty(Directory.GetFiles(_temp.Path, "history.json.broken-*"));
    }

    [Fact]
    public void AnIntegerEnumValue_CountsAsCorruption()
    {
        var path = _temp.File("history.json");
        File.WriteAllText(path, "[{\"id\":\"a\",\"mode\":99}]");

        var service = new HistoryService(path);

        Assert.Empty(service.Entries);
        Assert.Single(Directory.GetFiles(_temp.Path, "history.json.broken-*"));
    }
}
