using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Juvin.App.Services;
using Juvin.Core.Hotkeys;
using Juvin.Core.Interfaces;
using Juvin.Core.Localization;
using Juvin.Core.Models;

namespace Juvin.App.ViewModels.Sections;

/// <summary>The status overview and the newest rewrites kept in history.</summary>
/// <remarks>
///     Connector, hotkey, and settings events arrive on other threads and go through the <c>post</c>
///     marshaller given to the constructor.
/// </remarks>
public sealed partial class HomeSectionViewModel : ObservableObject
{
    private const int RecentCount = 5;

    private readonly ConnectorService _connector;
    private readonly IHistoryService _history;
    private readonly HotkeyService _hotkeys;
    private readonly bool _isMac;
    private readonly Localizer _localizer;
    private readonly ModelProbeService _probe;
    private readonly ISettingsService _settings;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    [ObservableProperty]
    private string _connectorStatus = "";

    [ObservableProperty]
    private string _modelStatus = "";

    [ObservableProperty]
    private string _shortcutStatus = "";

    /// <param name="history">Supplies the recent rewrites.</param>
    /// <param name="settings">Holds the processing mode, model, and shortcut.</param>
    /// <param name="connector">Reports which extensions are connected.</param>
    /// <param name="hotkeys">Reports whether the shortcut is being listened for.</param>
    /// <param name="probe">Labels the local server's location.</param>
    /// <param name="localizer">Supplies the section text.</param>
    /// <param name="post">Runs an action on the UI thread.</param>
    /// <param name="isMac">Whether the shortcut is labelled for macOS; defaults to the current OS.</param>
    public HomeSectionViewModel(
        IHistoryService history,
        ISettingsService settings,
        ConnectorService connector,
        HotkeyService hotkeys,
        ModelProbeService probe,
        Localizer localizer,
        Action<Action> post,
        bool? isMac = null
    )
    {
        _history = history;
        _settings = settings;
        _connector = connector;
        _hotkeys = hotkeys;
        _probe = probe;
        _localizer = localizer;
        _isMac = isMac ?? OperatingSystem.IsMacOS();
        RefreshRecent();
        RefreshConnectorStatus();
        RefreshModelStatus();
        RefreshShortcutStatus();
        history.Changed += RefreshRecent;
        settings.SettingsChanged += _ => post(() =>
        {
            OnPropertyChanged(nameof(RecentHint));
            RefreshModelStatus();
            RefreshShortcutStatus();
        });
        connector.ClientsChanged += () => post(RefreshConnectorStatus);
        hotkeys.StatusChanged += () => post(RefreshShortcutStatus);
    }

    public ObservableCollection<RecentRewriteRow> RecentRewrites { get; } = [];

    public bool HasRecentRewrites => RecentRewrites.Count > 0;
    public bool HasError => ErrorMessage is not null;

    /// <summary>Shown in place of the list when it is empty.</summary>
    public string RecentHint =>
        !_settings.Current.HistoryEnabled ? _localizer["Home.RecentHint"]
        : !_history.IsAvailable ? _localizer["Home.RecentUnavailable"]
        : _localizer["Home.RecentEmpty"];

    /// <summary>Writes text to the clipboard; set by the view, which owns the clipboard.</summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

    [RelayCommand]
    private async Task CopyAsync(RecentRewriteRow? row)
    {
        if (row is null || CopyToClipboard is null)
        {
            return;
        }

        await CopyToClipboard(row.Result);
        Trace.WriteLine($"[HomeSection] Copied {row.Result.Length} chars from history entry {row.Id}.");
    }

    [RelayCommand]
    private void Delete(RecentRewriteRow? row)
    {
        if (row is null)
        {
            return;
        }

        ErrorMessage = null;
        try
        {
            _history.Delete(row.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"[HomeSection] Could not delete history entry {row.Id}: {ex.GetType().Name}");
            ErrorMessage = _localizer.Format("Home.DeleteFailed", ex.Message);
        }
    }

    private void RefreshRecent()
    {
        RecentRewrites.Clear();
        foreach (var entry in _history.Entries.Take(RecentCount))
        {
            RecentRewrites.Add(CreateRow(entry));
        }

        OnPropertyChanged(nameof(HasRecentRewrites));
        OnPropertyChanged(nameof(RecentHint));
    }

    private void RefreshConnectorStatus()
    {
        var names = _connector.Clients.Select(session => BrowserLabels.Describe(session.Client!.Browser.Name, _localizer)).ToList();
        ConnectorStatus = names.Count == 0
            ? _localizer["Home.Status.NotConnected"]
            : _localizer.Format("Home.Status.Connected", string.Join(", ", names));
    }

    private void RefreshModelStatus()
    {
        var settings = _settings.Current;
        if (settings.ProcessingMode == ProcessingMode.Codex)
        {
            var codex = _localizer["Models.Mode.Codex"];
            ModelStatus = string.IsNullOrWhiteSpace(settings.CodexModel) ? codex : _localizer.Format("Home.Status.Model", codex, settings.CodexModel);
            return;
        }

        var location = _probe.LocationLabel(settings.LocalServerUrl);
        var model = string.IsNullOrWhiteSpace(settings.LocalModel) ? null : settings.LocalModel;
        ModelStatus = (location, model) switch
        {
            ("", null) => _localizer["Models.NoModel"],
            ("", { } name) => name,
            (_, null) => _localizer.Format("Home.Status.NoModel", location),
            (_, { } name) => _localizer.Format("Home.Status.Model", location, name),
        };
    }

    private void RefreshShortcutStatus()
    {
        var shortcut = _settings.Current.RewriteShortcut;
        var chord = KeyChord.TryParse(shortcut, out var parsed) ? parsed.ToDisplayString(_isMac) : shortcut;
        ShortcutStatus = _localizer.Format(
            _hotkeys.Status.Kind == HotkeyStatusKind.Active ? "Home.Status.Listening" : "Home.Status.NotListening",
            chord
        );
    }

    private RecentRewriteRow CreateRow(HistoryEntry entry) =>
        new(
            entry.Id,
            entry.ActionTitle,
            string.IsNullOrWhiteSpace(entry.Subject) ? _localizer["Home.NoSubject"] : entry.Subject,
            _localizer.Format(
                "Home.RecentDetail",
                entry.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
                AdapterLabels.Describe(entry.Adapter, _localizer)
            ),
            entry.Applied,
            entry.Result
        );
}

/// <summary>A history entry as the Home list shows it. Displayed only, never traced.</summary>
/// <param name="Detail">When it ran and on which kind of page.</param>
public sealed record RecentRewriteRow(string Id, string Title, string Subject, string Detail, bool Applied, string Result);
