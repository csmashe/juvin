// ReSharper disable UnusedMemberInSuper.Global -- part of the platform contract implemented per OS; consumed by the provider settings once the UI lands

namespace Juvin.Platform;

/// <summary>
///     Per-user secret storage. Names must satisfy <see cref="SecretName.ThrowIfInvalid" />.
/// </summary>
public interface ISecretStore
{
    /// <summary>Returns the secret, or null when none is stored under <paramref name="name" />.</summary>
    /// <exception cref="System.Security.Cryptography.CryptographicException">
    ///     The stored secret is damaged or cannot be decrypted, or the platform store refused the request.
    /// </exception>
    Task<string?> GetAsync(string name);

    Task SetAsync(string name, string value);

    /// <summary>Removes the secret; succeeds when none exists.</summary>
    Task DeleteAsync(string name);
}
