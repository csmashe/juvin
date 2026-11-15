using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Juvin.App.ViewModels;
using Juvin.Core.Rewrite;
using Juvin.Platform;

namespace Juvin.App.Views;

/// <summary>
///     The rewrite panel: a borderless topmost window at the top-right of the primary screen,
///     owned by no other window so it shows while the main window sits in the tray. Created once
///     and never closed until the app exits; visibility is driven with <see cref="Window.Show()" />
///     and <see cref="Window.Hide" />.
/// </summary>
/// <remarks>
///     Checked on GNOME Wayland (XWayland): a shown-again window comes back at the right place,
///     but Mutter drops its above, skip-taskbar, and sticky states on hide and Avalonia does not
///     send them again, so <see cref="ShowPanel" /> re-asserts all three on every show.
/// </remarks>
public partial class RewritePanelWindow : Window
{
    private const int ScreenMargin = 16;
    private const double MaxPanelHeight = 640;

    private readonly IPanelWindowSupport? _support;
    private readonly RewritePanelViewModel? _viewModel;
    private IReadOnlyList<DiffSegment>? _renderedChanges;
    private bool _prepared;

    public RewritePanelWindow()
    {
        InitializeComponent();
    }

    public RewritePanelWindow(RewritePanelViewModel viewModel, IPanelWindowSupport support)
        : this()
    {
        _viewModel = viewModel;
        _support = support;
        DataContext = viewModel;
        viewModel.CopyToClipboard = CopyAsync;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    /// <summary>Shows the panel in its corner and brings it forward.</summary>
    public void ShowPanel()
    {
        Place();
        if (!_prepared)
        {
            _prepared = true;
            var native = TryGetPlatformHandle();
            _support?.PrepareWindow(native?.Handle ?? 0, native?.HandleDescriptor);
        }

        _support?.BeforeShow();
        if (!IsVisible)
        {
            ReassertStates();
            Show();
        }

        Activate();
        // Once mapped: the window manager may have moved it.
        Dispatcher.UIThread.Post(() =>
        {
            Place();
            var handle = TryGetPlatformHandle();
            _support?.ShowOnAllWorkspaces(handle?.Handle ?? 0, handle?.HandleDescriptor, Title ?? "");
        });
    }

    public void HidePanel()
    {
        if (_support is null)
        {
            Hide();
            return;
        }

        _support.Hide(Hide);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Closing by the user only hides the panel; it is reused for the next capture.
        if (e is { CloseReason: WindowCloseReason.WindowClosing, IsProgrammatic: false } && _viewModel is not null)
        {
            e.Cancel = true;
            _viewModel.CloseCommand.Execute(null);
        }

        base.OnClosing(e);
    }

    /// <remarks>
    ///     Avalonia sends only a state that changes, hence the toggles. Before the show, X11 gets them in the unmapped
    ///     window's <c>_NET_WM_STATE</c>, read by the window manager on map; after it, that read-modify-write could
    ///     drop the focused state KWin had just written, and Avalonia then never reports the panel active.
    /// </remarks>
    private void ReassertStates()
    {
        Topmost = false;
        Topmost = true;
        ShowInTaskbar = true;
        ShowInTaskbar = false;
    }

    /// <remarks>Fits the height to the working area, up to the designed <see cref="MaxPanelHeight" />, so the buttons stay on screen.</remarks>
    private void Place()
    {
        var screen = Screens.Primary ?? (Screens.All.Count > 0 ? Screens.All[0] : null);
        if (screen is null)
        {
            return;
        }

        // Position and the working area are in desktop pixels: device pixels on Windows and X11, points on macOS.
        var scale = DesktopScaling;
        var area = screen.WorkingArea;
        Height = Math.Min(area.Height / scale - 2 * ScreenMargin, MaxPanelHeight);
        var width = (int)Math.Round(Width * scale);
        var margin = (int)Math.Round(ScreenMargin * scale);
        Position = new PixelPoint(area.Right - width - margin, area.Y + margin);
    }

    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private async Task CopyAsync(string text)
    {
        if (Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RewritePanelViewModel.Changes) && _viewModel is not null)
        {
            RenderChanges(_viewModel.Changes);
        }
    }

    private void RenderChanges(IReadOnlyList<DiffSegment> changes)
    {
        if (ReferenceEquals(changes, _renderedChanges))
        {
            return;
        }

        _renderedChanges = changes;
        var inlines = new InlineCollection();
        foreach (var segment in changes)
        {
            var run = new Run(segment.Text);
            var style = segment.Kind switch
            {
                DiffKind.Removed => "diff-removed",
                DiffKind.Added => "diff-added",
                _ => null,
            };
            if (style is not null)
            {
                run.Classes.Add(style);
            }

            inlines.Add(run);
        }

        ChangesText.Inlines = inlines;
    }
}
