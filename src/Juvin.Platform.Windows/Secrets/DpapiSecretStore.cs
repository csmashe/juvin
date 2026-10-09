using System.Security.Cryptography;
using System.Text;
using Juvin.Core;
using Juvin.Core.Services;

namespace Juvin.Platform.Windows.Secrets;

/// <summary>Stores each secret as its own DPAPI-protected file (current-user scope) under the Secrets directory.</summary>
public sealed class DpapiSecretStore : ISecretStore
{
    private readonly string _directory;

    public DpapiSecretStore()
        : this(JuvinEnvironment.SecretsPath)
    {
    }

    public DpapiSecretStore(string secretsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretsDirectory);

        _directory = secretsDirectory;
    }

    public Task<string?> GetAsync(string name)
    {
        var path = PathFor(name);
        byte[] stored;
        try
        {
            stored = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return Task.FromResult<string?>(null);
        }

        var plain = ProtectedData.Unprotect(stored, Entropy(name), DataProtectionScope.CurrentUser);
        try
        {
            return Task.FromResult<string?>(Encoding.UTF8.GetString(plain));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public Task SetAsync(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var path = PathFor(name);

        var plain = Encoding.UTF8.GetBytes(value);
        try
        {
            Directory.CreateDirectory(_directory);
            AtomicFile.WriteAllBytes(
                path,
                ProtectedData.Protect(plain, Entropy(name), DataProtectionScope.CurrentUser)
            );
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(string name)
    {
        var path = PathFor(name);
        if (Directory.Exists(_directory))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private string PathFor(string name)
    {
        SecretName.ThrowIfInvalid(name);
        return Path.Join(_directory, name);
    }

    // Binds each blob to its name so one file cannot be swapped in for another.
    private static byte[] Entropy(string name) => Encoding.UTF8.GetBytes("Juvin.Secret:" + name);
}
