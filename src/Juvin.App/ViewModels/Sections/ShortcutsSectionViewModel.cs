using CommunityToolkit.Mvvm.ComponentModel;

namespace Juvin.App.ViewModels.Sections;

public sealed class ShortcutsSectionViewModel(ShortcutPanelViewModel panel) : ObservableObject
{
    public ShortcutPanelViewModel Panel { get; } = panel;
}
