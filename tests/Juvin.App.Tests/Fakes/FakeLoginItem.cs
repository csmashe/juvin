using Juvin.Platform;

namespace Juvin.App.Tests.Fakes;

/// <summary>An in-memory login item. Enable and Disable throw a <see cref="LoginItemException" /> while <see cref="Fail" /> is set.</summary>
public sealed class FakeLoginItem : ILoginItem
{
    public bool IsSupported { get; set; } = true;

    public bool Fail { get; set; }

    public bool Enabled { get; private set; }

    public int EnableCount { get; private set; }

    public int DisableCount { get; private set; }

    public Task EnableAsync(CancellationToken ct)
    {
        EnableCount++;
        return Change(true);
    }

    public Task DisableAsync(CancellationToken ct)
    {
        DisableCount++;
        return Change(false);
    }

    private Task Change(bool enabled)
    {
        if (Fail)
        {
            return Task.FromException(new LoginItemException("refused", new UnauthorizedAccessException()));
        }

        Enabled = enabled;
        return Task.CompletedTask;
    }
}
