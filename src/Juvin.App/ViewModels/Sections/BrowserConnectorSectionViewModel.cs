using CommunityToolkit.Mvvm.ComponentModel;

namespace Juvin.App.ViewModels.Sections;

public sealed class BrowserConnectorSectionViewModel(BrowserConnectorPanelViewModel panel) : ObservableObject
{
    public BrowserConnectorPanelViewModel Panel { get; } = panel;
}
