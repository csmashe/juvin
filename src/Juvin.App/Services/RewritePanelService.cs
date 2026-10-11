using Juvin.App.ViewModels;
using Juvin.App.Views;
using Juvin.Core.Connector;

namespace Juvin.App.Services;

/// <summary>Shows the rewrite panel for every capture the extension's shortcut produces.</summary>
public sealed class RewritePanelService
{
    private readonly ConnectorService _connector;
    private readonly Action _hidePanel;
    private readonly Action<Action> _post;
    private readonly Action _showPanel;
    private readonly RewritePanelViewModel _viewModel;

    // Read and written on the UI thread only.
    private long _shownCapture;

    /// <param name="connector">Raises the captures the panel shows.</param>
    /// <param name="viewModel">The panel's state.</param>
    /// <param name="window">The panel window to show and hide.</param>
    /// <param name="post">Runs an action on the UI thread.</param>
    public RewritePanelService(
        ConnectorService connector,
        RewritePanelViewModel viewModel,
        RewritePanelWindow window,
        Action<Action> post
    )
        : this(connector, viewModel, window.ShowPanel, window.HidePanel, post)
    {
    }

    internal RewritePanelService(
        ConnectorService connector,
        RewritePanelViewModel viewModel,
        Action showPanel,
        Action hidePanel,
        Action<Action> post
    )
    {
        _connector = connector;
        _viewModel = viewModel;
        _showPanel = showPanel;
        _hidePanel = hidePanel;
        _post = post;
    }

    public void Start()
    {
        _viewModel.HideRequested += _hidePanel;
        _connector.CaptureCompleted += OnCaptureCompleted;
    }

    /// <summary>Cancels a running rewrite so a Codex child process does not outlive the app.</summary>
    public void Stop()
    {
        _connector.CaptureCompleted -= OnCaptureCompleted;
        _viewModel.CloseCommand.Execute(null);
    }

    // Raised on a thread-pool thread by the connector. Two captures can pass the connector's
    // latest-capture check and then post in either order, so the UI thread checks again.
    internal void OnCaptureCompleted(ConnectorSession session, long number, ContextResult result) =>
        _post(() =>
        {
            if (number < _shownCapture)
            {
                return;
            }

            _shownCapture = number;
            _showPanel();
            _ = _viewModel.LoadAsync(new ConnectorRewriteTarget(session), result);
        });
}
