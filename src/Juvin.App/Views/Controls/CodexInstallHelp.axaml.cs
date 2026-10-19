using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Juvin.App.Services;

namespace Juvin.App.Views.Controls;

public partial class CodexInstallHelp : UserControl
{
    public CodexInstallHelp()
    {
        InitializeComponent();
    }

    // ReSharper disable once AsyncVoidEventHandlerMethod -- Avalonia click handlers must return void; every failure is caught below
    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        try
        {
            await clipboard.SetTextAsync(ModelProbeService.CodexInstallCommand);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[CodexInstallHelp] Could not copy the install command: {ex.GetType().Name}");
        }
    }
}
