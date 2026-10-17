using Juvin.Core.Models;

namespace Juvin.Core.Interfaces;

public interface ISettingsService
{
    AppSettings Current { get; }

    event Action<AppSettings>? SettingsChanged;

    /// <summary>
    ///     Raised by <see cref="Reset" /> after the defaults are in place, so holders of unsaved edits
    ///     can drop them instead of saving the old values again.
    /// </summary>
    event Action? SettingsReset;

    /// <summary>Re-reads the settings file, recovering from the backup or defaults if it is unreadable.</summary>
    void Load();

    /// <summary>
    ///     Applies <paramref name="update" /> to the current settings and saves the result.
    ///     <see cref="SettingsChanged" /> is raised only when the saved value differs.
    /// </summary>
    void Update(Func<AppSettings, AppSettings> update);

    /// <summary>Returns every setting to its default and removes the backup copies of the old values.</summary>
    void Reset();
}
