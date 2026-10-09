using System.Text;
using Juvin.Core;
using Juvin.Core.Services;

namespace Juvin.Platform.Linux.Secrets;

/// <summary>
///     Stores each secret as its own AES-GCM encrypted file under the Secrets directory, keyed by
///     a per-install key file.
/// </summary>
public sealed class LinuxSecretStore : ISecretStore
{
    private readonly string _directory;
    private readonly string _keyFilePath;

    public LinuxSecretStore()
        : this(JuvinEnvironment.SecretsPath, JuvinEnvironment.SecretKeyFilePath)
    {
    }

    public LinuxSecretStore(string secretsDirectory, string keyFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretsDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyFilePath);

        _directory = secretsDirectory;
        _keyFilePath = keyFilePath;
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

        return Task.FromResult<string?>(
            FileKeySecretProtection.Decrypt(stored, AssociatedData(name), _keyFilePath)
        );
    }

    public Task SetAsync(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var path = PathFor(name);

        JuvinEnvironment.CreatePrivateDirectory(_directory);
        AtomicFile.WriteAllBytes(
            path,
            FileKeySecretProtection.Encrypt(value, AssociatedData(name), _keyFilePath)
        );
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

    private static byte[] AssociatedData(string name) => Encoding.UTF8.GetBytes("Juvin.Secret:" + name);
}
