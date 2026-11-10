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
    private readonly Action<ConnectorSession?> _aimHotkey;
    private readonly Action _hidePanel;
    private readonly Action<Action> _post;
    private readonly Action _showPanel;
    private readonly RewritePanelViewModel _viewModel;

    // Read and written on the UI thread only.
    private bool _active;
    private long _shownCapture;
    private ConnectorSession? _shownSession;

    /// <param name="viewModel">The panel's state.</param>
    /// <param name="window">The panel window to show and hide.</param>
    /// <param name="connector">Told which browser the desktop hotkey is aimed at while the panel has focus.</param>
    /// <param name="post">Runs an action on the UI thread.</param>
    public RewritePanelService(RewritePanelViewModel viewModel, RewritePanelWindow window, ConnectorService connector, Action<Action> post)
        : this(viewModel, window.ShowPanel, window.HidePanel, post, connector.SetFocusedPanelSession)
    {
        window.Activated += (_, _) => SetActive(true);
        window.Deactivated += (_, _) => SetActive(false);
    }

    internal RewritePanelService(
        RewritePanelViewModel viewModel,
        Action showPanel,
        Action hidePanel,
        Action<Action> post,
        Action<ConnectorSession?>? aimHotkey = null
    )
    {
        _viewModel = viewModel;
        _showPanel = showPanel;
        _hidePanel = hidePanel;
        _post = post;
        _aimHotkey = aimHotkey ?? (_ => { });
    }

    public void Start() => _viewModel.HideRequested += OnHideRequested;

    /// <summary>Called on the UI thread when the panel window gains or loses keyboard focus.</summary>
    internal void SetActive(bool active)
    {
        _active = active;
        AimHotkey();
    }

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
            _shownSession = (target as ConnectorRewriteTarget)?.Session;
            AimHotkey();
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
            _shownSession = null;
            AimHotkey();
            _showPanel();
            _viewModel.ShowResult(capture, session, action, result);
        });

    private void OnHideRequested()
    {
        _shownSession = null;
        AimHotkey();
        _hidePanel();
    }

    // A capture no browser answered (none focused or connected, or a timeout) has no target, so nothing to aim at.
    private void AimHotkey() => _aimHotkey(_active ? _shownSession : null);
}
