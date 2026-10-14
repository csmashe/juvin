using Juvin.App.ViewModels;
using Juvin.App.Views;
using Juvin.Core.Connector;
using Juvin.Core.Rewrite;

namespace Juvin.App.Services;

/// <summary>
///     Shows the rewrite panel for the captures the action menu hands it: Review, a declined capture, a page that
///     cannot draw the menu, or a finished result the browser disconnected before it could take.
/// </summary>
public sealed class RewritePanelService
{
    private readonly Action _hidePanel;
    private readonly Action<Action> _post;
    private readonly Action _showPanel;
    private readonly RewritePanelViewModel _viewModel;

    // Read and written on the UI thread only.
    private long _shownCapture;

    /// <param name="viewModel">The panel's state.</param>
    /// <param name="window">The panel window to show and hide.</param>
    /// <param name="post">Runs an action on the UI thread.</param>
    public RewritePanelService(RewritePanelViewModel viewModel, RewritePanelWindow window, Action<Action> post)
        : this(viewModel, window.ShowPanel, window.HidePanel, post)
    {
    }

    internal RewritePanelService(RewritePanelViewModel viewModel, Action showPanel, Action hidePanel, Action<Action> post)
    {
        _viewModel = viewModel;
        _showPanel = showPanel;
        _hidePanel = hidePanel;
        _post = post;
    }

    public void Start() => _viewModel.HideRequested += _hidePanel;

    /// <summary>Cancels a running rewrite so a Codex child process does not outlive the app.</summary>
    public void Stop() => _viewModel.CloseCommand.Execute(null);

    /// <summary>Cancels what the panel is working on and hides it. Callable from any thread.</summary>
    public void Dismiss() => _post(() => _viewModel.CloseCommand.Execute(null));

    /// <summary>
    ///     Shows capture <paramref name="number" /> idle, unless a later one is already showing. Callable from
    ///     any thread: two captures can post in either order, so the UI thread checks the order again.
    ///     A null <paramref name="target" /> disables Recapture.
    /// </summary>
    public void Show(IRewriteTarget? target, long number, ContextResult result) =>
        _post(() =>
        {
            if (number < _shownCapture)
            {
                return;
            }

            _shownCapture = number;
            _showPanel();
            _ = _viewModel.LoadAsync(target, result);
        });

    /// <summary>
    ///     Shows a result the action menu finished for capture <paramref name="number" /> but could not deliver
    ///     because the browser disconnected, so it can still be copied. Callable from any thread.
    /// </summary>
    public void ShowResult(long number, ContextResult capture, RewriteSession session, RewriteAction action, RewriteResult result) =>
        _post(() =>
        {
            if (number < _shownCapture)
            {
                return;
            }

            _shownCapture = number;
            _showPanel();
            _viewModel.ShowResult(capture, session, action, result);
        });
}
