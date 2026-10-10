using Avalonia;
using Avalonia.Controls;
using Juvin.App.ViewModels.Sections;

namespace Juvin.App.Views.Sections;

public partial class BrowserConnectorSection : UserControl
{
    public BrowserConnectorSection()
    {
        InitializeComponent();
    }

    // Registrations can change outside the app (or in the wizard), so each visit re-detects.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        (DataContext as BrowserConnectorSectionViewModel)?.Panel.DetectCommand.Execute(null);
    }
}
