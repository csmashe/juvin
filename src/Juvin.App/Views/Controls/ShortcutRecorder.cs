using Avalonia.Controls;
using Avalonia.Input;
using Juvin.App.ViewModels;

namespace Juvin.App.Views.Controls;

/// <summary>
///     A focusable box that records the rewrite shortcut: focusing it starts recording, the next chord
///     is saved, and Escape or leaving it cancels. The recording logic lives in <see cref="ShortcutPanelViewModel" />.
/// </summary>
public sealed class ShortcutRecorder : Border
{
    public ShortcutRecorder()
    {
        Focusable = true;
    }

    private ShortcutPanelViewModel? ViewModel => DataContext as ShortcutPanelViewModel;

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        ViewModel?.BeginRecording();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        ViewModel?.CancelRecording();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (IsFocused)
        {
            ViewModel?.BeginRecording();
        }
        else
        {
            Focus(NavigationMethod.Pointer);
        }

        e.Handled = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        ViewModel?.HandleKeyUp(e.Key);
        base.OnKeyUp(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (ViewModel?.HandleKey(e.Key, e.KeyModifiers, e.PhysicalKey) == true)
        {
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }
}
