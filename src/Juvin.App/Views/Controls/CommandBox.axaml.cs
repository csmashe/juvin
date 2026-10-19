using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace Juvin.App.Views.Controls;

/// <summary>A shell command the user runs by hand, with a button that copies it.</summary>
public partial class CommandBox : UserControl
{
    public static readonly StyledProperty<string?> CommandProperty =
        AvaloniaProperty.Register<CommandBox, string?>(nameof(Command));

    public CommandBox()
    {
        InitializeComponent();
    }

    public string? Command
    {
        get => GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    // ReSharper disable once AsyncVoidEventHandlerMethod -- Avalonia click handlers must return void; every failure is caught below
    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(Command) || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        try
        {
            await clipboard.SetTextAsync(Command);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[CommandBox] Could not copy the command: {ex.GetType().Name}");
        }
    }
}
