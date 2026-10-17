using System.Text.Json;
using Juvin.Core.Interfaces;
using Juvin.Core.Models;

namespace Juvin.App.Tests.Fakes;

public sealed class FakeSettingsService : ISettingsService
{
    public AppSettings Current { get; private set; } = new();

    public Exception? UpdateFailure { get; set; }

    public int SaveCount { get; private set; }

    public event Action<AppSettings>? SettingsChanged;

    public event Action? SettingsReset;

    public void Load()
    {
    }

    public void Update(Func<AppSettings, AppSettings> update)
    {
        if (UpdateFailure is not null)
        {
            throw UpdateFailure;
        }

        var next = update(Current);
        // Like the real store: record equality would compare the collection properties by reference.
        if (JsonSerializer.Serialize(next) == JsonSerializer.Serialize(Current))
        {
            return;
        }

        Current = next;
        SaveCount++;
        SettingsChanged?.Invoke(next);
    }

    /// <summary>Delivers a notification without changing <see cref="Current" />, as a late notification from a concurrent update would.</summary>
    public void RaiseSettingsChanged(AppSettings settings) => SettingsChanged?.Invoke(settings);

    public void Reset()
    {
        Update(_ => new AppSettings());
        SettingsReset?.Invoke();
    }
}
