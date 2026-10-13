using Avalonia.Controls;
using Avalonia.Input.Platform;
using Juvin.App.ViewModels.Sections;

namespace Juvin.App.Views.Sections;

public partial class HomeSection : UserControl
{
    public HomeSection()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is HomeSectionViewModel viewModel)
            {
                viewModel.CopyToClipboard = CopyAsync;
            }
        };
    }

    private async Task CopyAsync(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }
}
