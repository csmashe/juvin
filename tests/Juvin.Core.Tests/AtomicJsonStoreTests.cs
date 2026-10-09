using Juvin.Core.Services;

namespace Juvin.Core.Tests;

public sealed class AtomicJsonStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        _temp.Dispose();
    }

    [Fact]
    public void Update_PersistsAndReloadsInAnotherStore()
    {
        var path = _temp.File("data.json");
        var store = new AtomicJsonStore<Sample>(path, () => new Sample("default", 0));

        store.Update(_ => new Sample("saved", 3));

        var reopened = new AtomicJsonStore<Sample>(path, () => new Sample("default", 0));
        Assert.Equal(new Sample("saved", 3), reopened.Reload());
    }

    [Fact]
    public void Update_SkipsWriteWhenNothingChanged()
    {
        var path = _temp.File("data.json");
        var writes = 0;
        var store = new AtomicJsonStore<Sample>(
            path,
            () => new Sample("default", 0),
            new AtomicJsonStoreOptions<Sample>(),
            (target, json) =>
            {
                writes++;
                AtomicFile.WriteAllText(target, json);
            }
        );

        store.Update(s => s with { Count = 1 });
        store.Update(s => s with { Count = 1 }, out var changed);

        Assert.False(changed);
        Assert.Equal(1, writes);
    }

    [Fact]
    public void Load_RecoversFromBackupWhenPrimaryIsCorrupt()
    {
        var path = _temp.File("data.json");
        var options = new AtomicJsonStoreOptions<Sample>
        {
            BackupMode = AtomicJsonBackupMode.LastKnownGood,
        };
        var store = new AtomicJsonStore<Sample>(path, () => new Sample("default", 0), options);
        store.Update(s => s with { Count = 1 });
        store.Update(s => s with { Count = 2 });

        File.WriteAllText(path, "{ not json");

        var recovered = new AtomicJsonStore<Sample>(path, () => new Sample("default", 0), options);
        Assert.Equal(1, recovered.Current.Count);
        Assert.True(File.Exists(path + ".bak"));
    }

    [Fact]
    public void Load_PreserveAndResetKeepsCorruptCopyAndReturnsDefault()
    {
        var path = _temp.File("data.json");
        File.WriteAllText(path, "{ not json");
        var diagnostics = new List<AtomicJsonStoreDiagnosticKind>();

        var store = new AtomicJsonStore<Sample>(
            path,
            () => new Sample("default", 0),
            new AtomicJsonStoreOptions<Sample>
            {
                CorruptFilePolicy = AtomicJsonCorruptFilePolicy.PreserveAndReset,
                Diagnostic = d => diagnostics.Add(d.Kind),
            }
        );

        Assert.Equal(new Sample("default", 0), store.Current);
        var preserved = Assert.Single(Directory.GetFiles(_temp.Path, "data.json.broken-*"));
        Assert.Equal("{ not json", File.ReadAllText(preserved));
        Assert.Contains(AtomicJsonStoreDiagnosticKind.CorruptFilePreserved, diagnostics);
    }

    [Fact]
    public void Load_ThrowPolicySurfacesCorruption()
    {
        var path = _temp.File("data.json");
        File.WriteAllText(path, "{ not json");

        var store = new AtomicJsonStore<Sample>(path, () => new Sample("default", 0));

        Assert.ThrowsAny<System.Text.Json.JsonException>(() => store.Current);
    }

    // ReSharper disable once NotAccessedPositionalProperty.Local -- compared through record equality and serialized by System.Text.Json
    private sealed record Sample(string Name, int Count);
}
