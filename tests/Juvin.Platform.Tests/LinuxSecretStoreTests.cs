using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Juvin.Platform.Linux.Secrets;

namespace Juvin.Platform.Tests;

[SupportedOSPlatform("linux")]
public sealed class LinuxSecretStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        _temp.Dispose();
    }

    private string KeyPath => _temp.File("secret.key");

    private string SecretsPath => _temp.File("Secrets");

    private LinuxSecretStore CreateStore() => new(SecretsPath, KeyPath);

    [LinuxFact]
    public async Task SetThenGet_RoundTripsAndOverwrites()
    {
        var store = CreateStore();

        await store.SetAsync("codex-token", "first");
        await store.SetAsync("codex-token", "second value");

        Assert.Equal("second value", await CreateStore().GetAsync("codex-token"));
    }

    [LinuxFact]
    public async Task Get_ReturnsNullWhenMissing()
    {
        Assert.Null(await CreateStore().GetAsync("missing"));
    }

    [LinuxFact]
    public async Task Delete_RemovesSecretAndToleratesMissing()
    {
        var store = CreateStore();
        await store.DeleteAsync("token");
        await store.SetAsync("token", "value");

        await store.DeleteAsync("token");
        await store.DeleteAsync("token");

        Assert.Null(await store.GetAsync("token"));
    }

    [LinuxFact]
    public async Task Set_CreatesOwnerOnlyKeyAndEncryptedFile()
    {
        await CreateStore().SetAsync("token", "plain-secret");

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(KeyPath));
        Assert.Equal(32, new FileInfo(KeyPath).Length);

        var stored = await File.ReadAllBytesAsync(Path.Join(SecretsPath, "token"));
        Assert.Equal("JVSP"u8.ToArray(), stored[..4]);
        Assert.Equal(1, stored[4]);
        Assert.DoesNotContain("plain-secret", Encoding.UTF8.GetString(stored));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(Path.Join(SecretsPath, "token"))
        );
    }

    [LinuxFact]
    public async Task Get_RejectsAFileCopiedFromAnotherName()
    {
        var store = CreateStore();
        await store.SetAsync("one", "value");
        File.Copy(Path.Join(SecretsPath, "one"), Path.Join(SecretsPath, "two"));

        await Assert.ThrowsAnyAsync<CryptographicException>(() => store.GetAsync("two"));
    }

    [LinuxFact]
    public async Task Get_RejectsAKeyFileWithLooseMode()
    {
        var store = CreateStore();
        await store.SetAsync("token", "value");
        File.SetUnixFileMode(KeyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

        await Assert.ThrowsAnyAsync<CryptographicException>(() => store.GetAsync("token"));
    }

    [LinuxFact]
    public async Task Set_RejectsAKeyFileSymlink()
    {
        var real = _temp.File("real.key");
        await File.WriteAllBytesAsync(real, new byte[32]);
        File.SetUnixFileMode(real, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.CreateSymbolicLink(KeyPath, real);

        await Assert.ThrowsAnyAsync<CryptographicException>(() => CreateStore().SetAsync("token", "value"));
    }

    [LinuxFact]
    public async Task Set_RejectsNamesThatCouldEscapeTheDirectory()
    {
        var store = CreateStore();

        await Assert.ThrowsAsync<ArgumentException>(() => store.SetAsync("../escape", "value"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SetAsync(".hidden", "value"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SetAsync("Token", "value"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SetAsync("token.", "value"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SetAsync("con.key", "value"));
    }
}
