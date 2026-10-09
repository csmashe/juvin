using System.Collections.Concurrent;

namespace Juvin.Platform.Tests.Fakes;

public sealed class FakeSecretStore : ISecretStore
{
    private readonly ConcurrentDictionary<string, string> _secrets = new(StringComparer.Ordinal);

    public Task<string?> GetAsync(string name)
    {
        SecretName.ThrowIfInvalid(name);
        return Task.FromResult(_secrets.GetValueOrDefault(name));
    }

    public Task SetAsync(string name, string value)
    {
        SecretName.ThrowIfInvalid(name);
        _secrets[name] = value;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string name)
    {
        SecretName.ThrowIfInvalid(name);
        _secrets.TryRemove(name, out _);
        return Task.CompletedTask;
    }
}
