using CommunityToolkit.Mvvm.ComponentModel;
using Juvin.Core.Interfaces;

namespace Juvin.App.ViewModels.Sections;

public sealed partial class ShortcutsSectionViewModel : ObservableObject
{
    [ObservableProperty]
    private string _rewriteShortcut;

    public ShortcutsSectionViewModel(ISettingsService settings)
    {
        _rewriteShortcut = settings.Current.RewriteShortcut;
        settings.SettingsChanged += s => RewriteShortcut = s.RewriteShortcut;
    }
}
