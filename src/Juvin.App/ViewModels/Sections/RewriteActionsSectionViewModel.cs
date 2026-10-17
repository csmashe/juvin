using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Juvin.Core.Interfaces;
using Juvin.Core.Localization;
using Juvin.Core.Models;
using Juvin.Core.Rewrite;

namespace Juvin.App.ViewModels.Sections;

/// <summary>
///     Edits the built-in instructions and the user's own actions. Text that fails validation, or
///     that could not be saved, stays in its box so the user can fix or retry it.
/// </summary>
public sealed partial class RewriteActionsSectionViewModel : ObservableObject
{
    private readonly Localizer _localizer;
    private readonly ISettingsService _settings;
    private bool _applyingSettings;
    private bool _rowsUnsaved;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSaveError))]
    private string? _saveError;

    public RewriteActionsSectionViewModel(ISettingsService settings, Localizer localizer)
    {
        _settings = settings;
        _localizer = localizer;
        BuiltInActions =
        [
            .. RewriteAction.BuiltIn
                .Where(action => !action.IsCustom)
                .Select(action => new BuiltInActionRow(
                    action,
                    action.DisplayName(localizer),
                    action.IsMissing ? localizer["RewriteActions.MissingHint"] : null,
                    OnRowCommitted
                )),
        ];
        CustomActions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasCustomActions));
        ApplySettings(settings.Current);
        settings.SettingsChanged += ApplySettings;
        settings.SettingsReset += OnSettingsReset;
    }

    public IReadOnlyList<BuiltInActionRow> BuiltInActions { get; }
    public ObservableCollection<CustomActionRow> CustomActions { get; } = [];
    public bool HasCustomActions => CustomActions.Count > 0;
    public bool HasSaveError => SaveError is not null;

    [RelayCommand]
    private void AddCustomAction() =>
        CustomActions.Add(new CustomActionRow(RewriteActionCatalog.NewCustomId(), "", "", OnRowCommitted, RemoveCustomAction));

    [RelayCommand]
    private void RetrySave() => SaveRows();

    private void OnRowCommitted()
    {
        if (!_applyingSettings)
        {
            SaveRows();
        }
    }

    private void RemoveCustomAction(CustomActionRow row)
    {
        CustomActions.Remove(row);
        SaveRows();
    }

    /// <summary>
    ///     Saves what the rows hold. An invalid built-in instruction keeps its saved override; an
    ///     unfinished user action keeps its last saved version, or stays out of the settings when it
    ///     was never saved.
    /// </summary>
    private void SaveRows()
    {
        try
        {
            _rowsUnsaved = false;
            SaveError = null;
            _settings.Update(s =>
            {
                var overrides = s.RewriteInstructionOverrides.ToImmutableSortedDictionary(StringComparer.Ordinal);
                foreach (var row in BuiltInActions.Where(row => !row.IsInvalid))
                {
                    var instruction = row.Instruction.Trim();
                    overrides = instruction == row.DefaultInstruction ? overrides.Remove(row.Id) : overrides.SetItem(row.Id, instruction);
                }

                var actions = new List<CustomRewriteAction>();
                foreach (var row in CustomActions)
                {
                    // ReSharper disable once ConditionalAccessQualifierIsNonNullableAccordingToAPIContract -- a hand-edited file can hold null entries
                    var saved = s.CustomRewriteActions.FirstOrDefault(action => action?.Id == row.Id);
                    if (row.IsComplete)
                    {
                        actions.Add(new CustomRewriteAction(row.Id, row.Title.Trim(), row.Instruction.Trim()));
                    }
                    else if (saved is not null)
                    {
                        actions.Add(saved);
                    }
                }

                return s with { RewriteInstructionOverrides = overrides, CustomRewriteActions = actions };
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"[RewriteActionsSection] Could not save settings: {ex.GetType().Name}");
            _rowsUnsaved = true;
            SaveError = _localizer.Format("RewriteActions.SaveFailed", ex.Message);
        }
    }

    private void ApplySettings(AppSettings settings)
    {
        _applyingSettings = true;
        try
        {
            // Unsaved drafts stay in their boxes until Retry saves them.
            if (_rowsUnsaved)
            {
                return;
            }

            var actions = RewriteActionCatalog.Resolve(settings);
            foreach (var row in BuiltInActions.Where(row => !row.IsInvalid))
            {
                row.Instruction = actions.First(action => action.Id == row.Id).Instruction;
            }

            ApplyCustomActions([.. actions.Where(action => action.Title is not null)]);
        }
        finally
        {
            _applyingSettings = false;
        }
    }

    /// <summary>Delete all data must not leave edits a Retry or the next commit would save again.</summary>
    private void OnSettingsReset()
    {
        _rowsUnsaved = false;
        SaveError = null;
        _applyingSettings = true;
        try
        {
            foreach (var row in BuiltInActions)
            {
                row.Instruction = row.DefaultInstruction;
            }

            CustomActions.Clear();
        }
        finally
        {
            _applyingSettings = false;
        }

        ApplySettings(_settings.Current);
    }

    /// <summary>
    ///     Updates the rows in place so a save does not rebuild the boxes under the user's cursor.
    ///     Unfinished rows are kept; a finished row the settings no longer hold was removed elsewhere
    ///     and goes.
    /// </summary>
    private void ApplyCustomActions(IReadOnlyList<RewriteAction> saved)
    {
        var rows = new List<CustomActionRow>();
        foreach (var row in CustomActions)
        {
            var action = saved.FirstOrDefault(action => action.Id == row.Id);
            if (action is not null && row.IsComplete)
            {
                row.Title = action.Title!;
                row.Instruction = action.Instruction;
            }

            if (action is not null || !row.IsComplete)
            {
                rows.Add(row);
            }
        }

        rows.AddRange(
            saved
                .Where(action => rows.All(row => row.Id != action.Id))
                .Select(action => new CustomActionRow(action.Id, action.Title!, action.Instruction, OnRowCommitted, RemoveCustomAction))
        );

        if (rows.SequenceEqual(CustomActions))
        {
            return;
        }

        CustomActions.Clear();
        foreach (var row in rows)
        {
            CustomActions.Add(row);
        }
    }
}

/// <summary>A built-in action whose instruction can be edited and reset.</summary>
public sealed partial class BuiltInActionRow : ObservableObject
{
    private readonly Action _committed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEdited))]
    private string _instruction;

    [ObservableProperty]
    private bool _isInvalid;

    public BuiltInActionRow(RewriteAction action, string title, string? hint, Action committed)
    {
        Id = action.Id;
        Title = title;
        Hint = hint;
        DefaultInstruction = action.Instruction;
        _instruction = action.Instruction;
        _committed = committed;
    }

    public string Id { get; }
    public string Title { get; }
    public string? Hint { get; }
    public bool HasHint => Hint is not null;
    public string DefaultInstruction { get; }
    public bool IsEdited => Instruction.Trim() != DefaultInstruction;

    partial void OnInstructionChanged(string value)
    {
        IsInvalid = !RewriteActionCatalog.IsValidInstruction(value);
        _committed();
    }

    [RelayCommand]
    private void Reset() => Instruction = DefaultInstruction;
}

/// <summary>One of the user's own actions. It is saved once both its title and instruction are valid.</summary>
public sealed partial class CustomActionRow : ObservableObject
{
    private readonly Action _committed;
    private readonly Action<CustomActionRow> _removed;

    [ObservableProperty]
    private string _instruction;

    [ObservableProperty]
    private bool _isInstructionInvalid;

    [ObservableProperty]
    private bool _isTitleInvalid;

    [ObservableProperty]
    private string _title;

    public CustomActionRow(
        string id,
        string title,
        string instruction,
        Action committed,
        Action<CustomActionRow> removed
    )
    {
        Id = id;
        _title = title;
        _instruction = instruction;
        _committed = committed;
        _removed = removed;
    }

    public string Id { get; }

    public bool IsComplete =>
        RewriteActionCatalog.IsValidTitle(Title) && RewriteActionCatalog.IsValidInstruction(Instruction);

    partial void OnTitleChanged(string value)
    {
        IsTitleInvalid = !RewriteActionCatalog.IsValidTitle(value);
        _committed();
    }

    partial void OnInstructionChanged(string value)
    {
        IsInstructionInvalid = !RewriteActionCatalog.IsValidInstruction(value);
        _committed();
    }

    // The title box saves on focus loss; Enter commits through this.
    [RelayCommand]
    private void CommitTitle(string? text) => Title = text ?? "";

    [RelayCommand]
    private void Remove() => _removed(this);
}
