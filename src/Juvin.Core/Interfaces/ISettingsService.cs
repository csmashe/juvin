using Juvin.Core.Models;

namespace Juvin.Core.Interfaces;

public interface ISettingsService
{
    AppSettings Current { get; }

    event Action<AppSettings>? SettingsChanged;

    /// <summary>Re-reads the settings file, recovering from the backup or defaults if it is unreadable.</summary>
    void Load();

    /// <summary>
    ///     Applies <paramref name="update" /> to the current settings and saves the result.
    ///     <see cref="SettingsChanged" /> is raised only when the saved value differs.
    /// </summary>
    void Update(Func<AppSettings, AppSettings> update);
}
