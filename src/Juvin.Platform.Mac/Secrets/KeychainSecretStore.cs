using System.Security.Cryptography;
using System.Text;

namespace Juvin.Platform.Mac.Secrets;

/// <summary>
///     Stores each secret as a generic password in the user's login Keychain, service "Juvin",
///     account = secret name.
/// </summary>
public sealed class KeychainSecretStore : ISecretStore
{
    private const string Service = "Juvin";

    public Task<string?> GetAsync(string name)
    {
        SecretName.ThrowIfInvalid(name);

        var bytes = Keychain.Find(Service, name);
        if (bytes is null)
        {
            return Task.FromResult<string?>(null);
        }

        try
        {
            return Task.FromResult<string?>(Encoding.UTF8.GetString(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public Task SetAsync(string name, string value)
    {
        SecretName.ThrowIfInvalid(name);
        ArgumentNullException.ThrowIfNull(value);

        var bytes = Encoding.UTF8.GetBytes(value);
        try
        {
            Keychain.Upsert(Service, name, bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(string name)
    {
        SecretName.ThrowIfInvalid(name);

        Keychain.Delete(Service, name);
        return Task.CompletedTask;
    }
}
