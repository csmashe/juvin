using CommunityToolkit.Mvvm.ComponentModel;
using Juvin.Core.Interfaces;

namespace Juvin.App.ViewModels.Sections;

public sealed partial class ModelsSectionViewModel : ObservableObject
{
    [ObservableProperty]
    private string _localServerUrl;

    public ModelsSectionViewModel(ISettingsService settings)
    {
        _localServerUrl = settings.Current.LocalServerUrl;
        settings.SettingsChanged += s => LocalServerUrl = s.LocalServerUrl;
    }
}
