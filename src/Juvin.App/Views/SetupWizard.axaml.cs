using Avalonia.Controls;
using Juvin.App.ViewModels;

namespace Juvin.App.Views;

public partial class SetupWizard : Window
{
    public SetupWizard()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is SetupWizardViewModel viewModel)
        {
            viewModel.RequestClose += (_, _) => Close();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        (DataContext as SetupWizardViewModel)?.Dispose();
        base.OnClosed(e);
    }
}
