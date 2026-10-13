using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Juvin.Core.Interfaces;
using Juvin.Core.Localization;
using Juvin.Core.Models;

namespace Juvin.App.ViewModels.Sections;

/// <summary>The status overview and the newest rewrites kept in history.</summary>
public sealed partial class HomeSectionViewModel : ObservableObject
{
    private const int RecentCount = 5;

    private readonly IHistoryService _history;
    private readonly Localizer _localizer;
    private readonly ISettingsService _settings;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    public HomeSectionViewModel(IHistoryService history, ISettingsService settings, Localizer localizer)
    {
        _history = history;
        _settings = settings;
        _localizer = localizer;
        RefreshRecent();
        history.Changed += RefreshRecent;
        settings.SettingsChanged += _ => OnPropertyChanged(nameof(RecentHint));
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
