// ReSharper disable UnusedParameterInPartialMethod -- the property change hooks have their signature fixed by the source generator

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

/// <summary>Every change is saved at once. Traces carry action ids only, never prompts.</summary>
public sealed partial class RewriteActionsSectionViewModel : ObservableObject
{
    private readonly Localizer _localizer;
    private readonly ISettingsService _settings;
    private bool _applyingSettings;

    [ObservableProperty]
    private RewriteActionOption? _selectedDefault;

    [ObservableProperty]
    private LengthGuidanceOption? _selectedLengthGuidance;

    public RewriteActionsSectionViewModel(ISettingsService settings, Localizer localizer)
    {
        _settings = settings;
        _localizer = localizer;
        BuiltInActions =
        [
            .. RewriteAction.BuiltIn
                .Where(action => !action.IsFreeText)
                .Select(action => new BuiltInActionRow(action, localizer[action.TitleKey], SaveBuiltIn)),
        ];
        LengthGuidanceOptions =
        [
            new LengthGuidanceOption(LengthGuidance.None, localizer["RewriteActions.Length.None"]),
            new LengthGuidanceOption(LengthGuidance.SimilarLength, localizer["RewriteActions.Length.SimilarLength"]),
            new LengthGuidanceOption(LengthGuidance.Shorter, localizer["RewriteActions.Length.Shorter"]),
        ];
        ApplySettings(settings.Current);
        settings.SettingsChanged += ApplySettings;
    }

    public IReadOnlyList<BuiltInActionRow> BuiltInActions { get; }
    public ObservableCollection<CustomActionRow> CustomActions { get; } = [];
    public ObservableCollection<RewriteActionOption> DefaultOptions { get; } = [];
    public IReadOnlyList<LengthGuidanceOption> LengthGuidanceOptions { get; }

    partial void OnSelectedDefaultChanged(RewriteActionOption? value)
    {
        if (!_applyingSettings && value is not null)
        {
            Save(s => s with { DefaultRewriteAction = value.Id });
        }
    }

    partial void OnSelectedLengthGuidanceChanged(LengthGuidanceOption? value)
    {
        if (!_applyingSettings && value is not null)
        {
            Save(s => s with { RewriteLengthGuidance = value.Value });
        }
    }

    [RelayCommand]
    private void AddCustomAction()
    {
        var name = _localizer["RewriteActions.NewActionName"];
        Save(s =>
            s with
            {
                CustomRewriteActions =
                [
                    .. s.CustomRewriteActions,
                    new CustomRewriteAction(CustomRewriteAction.NewId(s.CustomRewriteActions.Select(a => a.Id)), name, ""),
                ],
            }
        );
    }

    private void SaveBuiltIn(BuiltInActionRow row)
    {
        if (_applyingSettings)
        {
            return;
        }

        var instruction = row.Instruction.Trim();
        Save(s =>
        {
            var prompts = s.RewriteActionPrompts.ToImmutableDictionary();
            return s with
            {
                RewriteActionPrompts = instruction.Length == 0 || instruction == row.DefaultInstruction
                    ? prompts.Remove(row.Id)
                    : prompts.SetItem(row.Id, instruction),
            };
        });
    }

    private void SaveCustom(CustomActionRow row)
    {
        if (_applyingSettings)
        {
            return;
        }

        var edited = new CustomRewriteAction(row.Id, row.Name.Trim(), row.Instruction.Trim());
        Save(s => s with { CustomRewriteActions = [.. s.CustomRewriteActions.Select(a => a.Id == row.Id ? edited : a)] });
    }

    private void RemoveCustom(CustomActionRow row) =>
        Save(s =>
            s with
            {
                CustomRewriteActions = [.. s.CustomRewriteActions.Where(a => a.Id != row.Id)],
                DefaultRewriteAction = s.DefaultRewriteAction == row.Id ? RewriteAction.Clearer.Id : s.DefaultRewriteAction,
            }
        );

    private void Save(Func<AppSettings, AppSettings> update)
    {
        try
        {
            _settings.Update(update);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"[RewriteActionsSection] Could not save settings: {ex.GetType().Name}");
            ApplySettings(_settings.Current);
        }
    }

    private void ApplySettings(AppSettings settings)
    {
        _applyingSettings = true;
        try
        {
            var catalog = new RewriteActionCatalog(settings);
            foreach (var row in BuiltInActions)
            {
                row.Instruction = catalog.Find(row.Id)?.Instruction ?? row.DefaultInstruction;
            }

            ApplyCustomActions(settings.CustomRewriteActions);

            DefaultOptions.Clear();
            foreach (var action in catalog.Pickable)
            {
                DefaultOptions.Add(new RewriteActionOption(action.Id, action.Title ?? _localizer[action.TitleKey]));
            }

            SelectedDefault = DefaultOptions.First(option => option.Id == catalog.Default.Id);
            SelectedLengthGuidance =
                LengthGuidanceOptions.FirstOrDefault(option => option.Value == settings.RewriteLengthGuidance)
                ?? LengthGuidanceOptions[0];
        }
        finally
        {
            _applyingSettings = false;
        }
    }

    /// <summary>Updates the rows in place when the set of actions is unchanged, so an edit in progress keeps its text box.</summary>
    private void ApplyCustomActions(IReadOnlyList<CustomRewriteAction> actions)
    {
        if (!actions.Select(a => a.Id).SequenceEqual(CustomActions.Select(row => row.Id)))
        {
            CustomActions.Clear();
            foreach (var action in actions)
            {
                CustomActions.Add(new CustomActionRow(action.Id, SaveCustom, RemoveCustom));
            }
        }

        for (var i = 0; i < actions.Count; i++)
        {
            CustomActions[i].Name = actions[i].Name;
            CustomActions[i].Instruction = actions[i].Instruction;
        }
    }
}

public sealed partial class BuiltInActionRow : ObservableObject
{
    private readonly Action<BuiltInActionRow> _changed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEdited))]
    private string _instruction = "";

    public BuiltInActionRow(RewriteAction action, string title, Action<BuiltInActionRow> changed)
    {
        _changed = changed;
        Id = action.Id;
        Title = title;
        DefaultInstruction = action.Instruction;
    }

    public string Id { get; }
    public string Title { get; }
    public string DefaultInstruction { get; }
    public bool IsEdited => Instruction.Trim() != DefaultInstruction;

    partial void OnInstructionChanged(string value) => _changed(this);

    [RelayCommand]
    private void Reset() => Instruction = DefaultInstruction;
}

/// <summary>Reaches the rewrite panel once both fields have text.</summary>
public sealed partial class CustomActionRow : ObservableObject
{
    private readonly Action<CustomActionRow> _changed;
    private readonly Action<CustomActionRow> _removed;

    [ObservableProperty]
    private string _instruction = "";

    [ObservableProperty]
    private string _name = "";

    public CustomActionRow(string id, Action<CustomActionRow> changed, Action<CustomActionRow> removed)
    {
        Id = id;
        _changed = changed;
        _removed = removed;
    }

    public string Id { get; }

    partial void OnNameChanged(string value) => _changed(this);

    partial void OnInstructionChanged(string value) => _changed(this);

    [RelayCommand]
    private void Remove() => _removed(this);
}

public sealed record RewriteActionOption(string Id, string Title);

public sealed record LengthGuidanceOption(LengthGuidance Value, string Label);
