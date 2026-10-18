using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Juvin.App.Services;
using Juvin.Core.Interfaces;
using Juvin.Core.Localization;
using Juvin.Core.Models;
using Juvin.Core.Providers;

namespace Juvin.App.ViewModels.Sections;

public sealed partial class ModelsSectionViewModel : ObservableObject
{
    private readonly ProbeGate _codexProbe = new();
    private readonly Localizer _localizer;
    private readonly ProbeGate _localProbe = new();
    private readonly ModelProbeService _probes;
    private readonly ISettingsService _settings;
    private bool _applyingSettings;
    private IReadOnlyList<ModelInfo> _probedModels = [];
    private ProviderStatus? _probedStatus;
    private string? _probedUrl;
    private (string? Path, string? Model)? _probedCodex;

    [ObservableProperty]
    private string _codexBinaryPath = "";

    [ObservableProperty]
    private string _codexModel = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCodexStatus))]
    private string? _codexStatus;

    [ObservableProperty]
    private bool _isCodexMissing;

    [ObservableProperty]
    private bool _isCodexProbing;

    [ObservableProperty]
    private bool _isLocalProbing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LocalServerLocation), nameof(HasLocalServerLocation), nameof(HasServerNameHint))]
    private string _localServerUrl = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLocalStatus))]
    private string? _localStatus;

    [ObservableProperty]
    private bool _localThinkingDisabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSaveError))]
    private string? _saveError;

    [ObservableProperty]
    private ModelOption? _selectedLocalModel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLocalMode), nameof(IsCodexMode), nameof(ModeHint))]
    private ProcessingModeOption? _selectedMode;

    public ModelsSectionViewModel(ISettingsService settings, ModelProbeService probes, Localizer localizer)
    {
        _settings = settings;
        _probes = probes;
        _localizer = localizer;
        ModeOptions = ProcessingModeOption.All(localizer);
        ApplySettings(settings.Current);
        settings.SettingsChanged += ApplySettings;
    }

    public IReadOnlyList<ProcessingModeOption> ModeOptions { get; }
    public ObservableCollection<ModelOption> LocalModels { get; } = [];

    public bool IsLocalMode => SelectedMode?.Mode == ProcessingMode.Local;
    public bool IsCodexMode => SelectedMode?.Mode == ProcessingMode.Codex;
    public string ModeHint => SelectedMode?.Hint ?? "";
    public string LocalServerLocation => _probes.LocationLabel(LocalServerUrl);
    public bool HasLocalServerLocation => LocalServerLocation.Length > 0;
    public bool HasServerNameHint => ModelProbeService.IsUnlistedHostName(LocalServerUrl);
    public bool HasSaveError => SaveError is not null;
    public bool HasLocalStatus => !string.IsNullOrWhiteSpace(LocalStatus);
    public bool HasCodexStatus => !string.IsNullOrWhiteSpace(CodexStatus);

    partial void OnSelectedModeChanged(ProcessingModeOption? value)
    {
        if (!_applyingSettings && value is not null)
        {
            Save(s => s with { ProcessingMode = value.Mode });
        }
    }

    partial void OnLocalServerUrlChanged(string value)
    {
        if (_applyingSettings)
        {
            return;
        }

        var url = value.Trim();
        if (ModelProbeService.IsThisDevice(url) is null)
        {
            LocalStatus = _localizer["Models.Status.InvalidUrl"];
            return;
        }

        LocalStatus = null;
        Save(s => s with { LocalServerUrl = url });
        _ = CheckLocalAsync();
    }

    partial void OnSelectedLocalModelChanged(ModelOption? value)
    {
        if (_applyingSettings || value is null)
        {
            return;
        }

        Save(s => s with { LocalModel = value.Id });
        if (_probedStatus is not null && _probedUrl is not null && ModelProbeService.IsSameServer(_probedUrl, _settings.Current.LocalServerUrl))
        {
            LocalStatus = _probes.DescribeLocal(ModelProbeService.WithSelectedModel(_probedStatus, _settings.Current.LocalModel));
        }
    }

    partial void OnLocalThinkingDisabledChanged(bool value)
    {
        if (!_applyingSettings)
        {
            Save(s => s with { LocalThinkingDisabled = value });
        }
    }

    partial void OnCodexBinaryPathChanged(string value)
    {
        if (!_applyingSettings)
        {
            Save(s => s with { CodexBinaryPath = NullIfBlank(value) });
        }
    }

    partial void OnCodexModelChanged(string value)
    {
        if (!_applyingSettings)
        {
            Save(s => s with { CodexModel = NullIfBlank(value) });
        }
    }

    // The text boxes save on focus loss; Enter commits through these.
    [RelayCommand]
    private void CommitLocalServerUrl(string? text) => LocalServerUrl = text ?? "";

    [RelayCommand]
    private void CommitCodexBinaryPath(string? text) => CodexBinaryPath = text ?? "";

    [RelayCommand]
    private void CommitCodexModel(string? text) => CodexModel = text ?? "";

    [RelayCommand]
    private async Task CheckLocalAsync()
    {
        var settings = _settings.Current;
        var url = settings.LocalServerUrl;
        // An invalid address is shown but never saved, so a check would probe the old one.
        if (ModelProbeService.IsThisDevice(LocalServerUrl.Trim()) is null)
        {
            LocalStatus = _localizer["Models.Status.InvalidUrl"];
            return;
        }

        IsLocalProbing = true;
        var status = await _localProbe.RunAsync(ct => _probes.ProbeLocalAsync(settings, url, ct), CancellationToken.None);
        if (status is null)
        {
            return;
        }

        IsLocalProbing = false;
        _probedUrl = url;
        _probedStatus = status;
        _probedModels = status.Models;
        RebuildModelList(_settings.Current);
        // The address or model may have changed while the check ran.
        if (ModelProbeService.IsThisDevice(LocalServerUrl.Trim()) is not null && ModelProbeService.IsSameServer(url, _settings.Current.LocalServerUrl))
        {
            LocalStatus = _probes.DescribeLocal(ModelProbeService.WithSelectedModel(status, _settings.Current.LocalModel));
        }
    }

    [RelayCommand]
    private async Task CheckCodexAsync()
    {
        var settings = _settings.Current;
        IsCodexProbing = true;
        var status = await _codexProbe.RunAsync(ct => _probes.ProbeCodexAsync(settings, ct), CancellationToken.None);
        if (status is null)
        {
            return;
        }

        IsCodexProbing = false;
        _probedCodex = (settings.CodexBinaryPath, settings.CodexModel);
        if (_probedCodex == (_settings.Current.CodexBinaryPath, _settings.Current.CodexModel))
        {
            CodexStatus = _probes.DescribeCodex(status);
            IsCodexMissing = ModelProbeService.IsCodexMissing(status);
        }
    }

    private void Save(Func<AppSettings, AppSettings> update)
    {
        try
        {
            _settings.Update(update);
            SaveError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"[ModelsSection] Could not save settings: {ex.GetType().Name}");
            SaveError = _localizer.Format("PrivacyAndData.SaveFailed", ex.Message);
            ApplySettings(_settings.Current);
        }
    }

    private void ApplySettings(AppSettings settings)
    {
        _applyingSettings = true;
        try
        {
            SelectedMode = ProcessingModeOption.Find(ModeOptions, settings.ProcessingMode);
            LocalServerUrl = settings.LocalServerUrl;
            LocalThinkingDisabled = settings.LocalThinkingDisabled;
            CodexBinaryPath = settings.CodexBinaryPath ?? "";
            CodexModel = settings.CodexModel ?? "";
            RebuildModelList(settings);
            // A status checked against settings that changed elsewhere is stale.
            if (_probedUrl is not null && !ModelProbeService.IsSameServer(_probedUrl, settings.LocalServerUrl))
            {
                LocalStatus = null;
            }
            else if (_probedStatus is not null && LocalStatus is not null)
            {
                LocalStatus = _probes.DescribeLocal(ModelProbeService.WithSelectedModel(_probedStatus, settings.LocalModel));
            }

            // ReSharper disable once InvertIf -- reads as one of the sibling staleness checks above
            if (_probedCodex is not null && _probedCodex != (settings.CodexBinaryPath, settings.CodexModel))
            {
                CodexStatus = null;
                IsCodexMissing = false;
            }
        }
        finally
        {
            _applyingSettings = false;
        }
    }

    /// <summary>
    ///     Lists the last probe's models for the configured server. The saved model is always listed,
    ///     even before a probe or when the server no longer has it, so the picker is never blank.
    /// </summary>
    private void RebuildModelList(AppSettings settings)
    {
        var wasApplying = _applyingSettings;
        _applyingSettings = true;
        try
        {
            var models =
                _probedUrl is not null && ModelProbeService.IsSameServer(_probedUrl, settings.LocalServerUrl)
                    ? _probedModels
                    : [];
            LocalModels.Clear();
            foreach (var model in ModelProbeService.UsableModels(models))
            {
                LocalModels.Add(ModelOption.From(model));
            }

            var selected = LocalModels.FirstOrDefault(option => ModelProbeService.IsSameModel(option.Id, settings.LocalModel));
            if (selected is null && !string.IsNullOrWhiteSpace(settings.LocalModel))
            {
                selected = new ModelOption(settings.LocalModel, settings.LocalModel, false);
                LocalModels.Insert(0, selected);
            }

            SelectedLocalModel = selected;
        }
        finally
        {
            _applyingSettings = wasApplying;
        }
    }

    private static string? NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
