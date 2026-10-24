using System.Diagnostics;
using Juvin.App.ViewModels;
using Juvin.Core.Connector;
using Juvin.Core.Interfaces;
using Juvin.Core.Localization;
using Juvin.Core.Providers;
using Juvin.Core.Rewrite;

namespace Juvin.App.Services;

/// <summary>
///     Owns the action menu (section 2.8 of the MVP scope): shows it over every capture, runs the
///     action the user picks, applies the result through the capture's hash and format checks, and reports each
///     state back with <c>menu.update</c>. Review, a declined capture, and a page that cannot draw the menu go to
///     the rewrite panel instead, as does a finished result the browser disconnected before it could take.
///     Traces carry menu ids, action ids, lengths, and error codes only.
/// </summary>
/// <remarks>
///     State is read and written on the UI thread only, through the <c>post</c> marshaller; the connector,
///     the provider, and the extension's replies arrive on thread-pool threads.
/// </remarks>
public sealed class ActionMenuService
{
    private const string DraftChangedCode = "draft-changed";
    private const string UnsupportedContentCode = "unsupported-content";

    private static readonly TimeSpan s_showTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan s_sendTimeout = TimeSpan.FromSeconds(10);

    private static readonly string[] s_slots =
    [
        RewriteAction.Missing.Id,
        RewriteAction.Soften.Id,
        RewriteAction.Clearer.Id,
        RewriteAction.Shorter.Id,
        RewriteAction.Grammar.Id,
    ];

    private readonly RewritePromptBuilder? _builder;
    private readonly ConnectorService _connector;
    private readonly IHistoryService _history;
    private readonly Localizer _localizer;
    private readonly RewritePanelService _panel;
    private readonly Action<Action> _post;
    private readonly ProviderSelector _providers;
    private readonly ISettingsService _settings;

    private long _latestCapture;
    private OpenMenu? _menu;
    private long _menuCount;

    /// <param name="connector">Raises the captures and the extension's menu choices.</param>
    /// <param name="panel">Takes the captures the menu hands over or cannot show.</param>
    /// <param name="providers">Builds the provider for each run from the current settings.</param>
    /// <param name="settings">The actions, the processing mode, and the history consent.</param>
    /// <param name="history">Where finished rewrites are kept when history is turned on.</param>
    /// <param name="localizer">The string table for titles, the status line, and errors.</param>
    /// <param name="post">Runs an action on the UI thread; tests pass one that runs it inline.</param>
    /// <param name="builder">Builds each run's prompt; null uses the default budget.</param>
    public ActionMenuService(
        ConnectorService connector,
        RewritePanelService panel,
        ProviderSelector providers,
        ISettingsService settings,
        IHistoryService history,
        Localizer localizer,
        Action<Action> post,
        RewritePromptBuilder? builder = null
    )
    {
        _connector = connector;
        _panel = panel;
        _providers = providers;
        _settings = settings;
        _history = history;
        _localizer = localizer;
        _post = post;
        _builder = builder;
    }

    /// <summary>Raised on the UI thread when the extension has drawn a menu.</summary>
    public event Action? MenuShown;

    /// <summary>Raised on the UI thread when a menu's rewrite has replaced the draft.</summary>
    public event Action? RewriteApplied;

    public void Start()
    {
        _connector.CaptureCompleted += OnCaptureCompleted;
        _connector.TriggerFailed += OnTriggerFailed;
        _connector.MenuChosen += OnMenuChosen;
        _connector.MenuClosed += OnMenuClosed;
    }

    /// <summary>
    ///     Cancels a running action so a Codex child process does not outlive the app, and closes the menu.
    ///     Called on the UI thread; the returned task ends when the close has been sent or abandoned, and
    ///     does not need the UI thread to finish.
    /// </summary>
    public Task Stop()
    {
        _connector.CaptureCompleted -= OnCaptureCompleted;
        _connector.TriggerFailed -= OnTriggerFailed;
        _connector.MenuChosen -= OnMenuChosen;
        _connector.MenuClosed -= OnMenuClosed;
        return Supersede();
    }

    // Two captures can post in either order, so the UI thread checks the order again. Recapture never
    // checks focus, so a target comes only from a focused browser's own answer.
    internal void OnCaptureCompleted(ConnectorSession session, long number, ContextResult result) =>
        _post(() =>
        {
            if (!TakeCapture(number))
            {
                return;
            }

            _ = Supersede();
            if (result.Capture is null)
            {
                var answered = ConnectorService.AnsweredByBrowser(result)
                    && result.Error?.Code != ConnectorService.BrowserNotFocusedCode;
                _panel.Show(answered ? new ConnectorRewriteTarget(session) : null, number, result);
                return;
            }

            // The panel may still be working on an earlier capture; a new press makes that stale.
            _panel.Dismiss();
            Show(new OpenMenu(NextMenuId(), session, new ConnectorRewriteTarget(session), number, result));
        });

    // No browser is connected, so there is no menu to draw and nothing to recapture from.
    private void OnTriggerFailed(long number, ContextResult result) =>
        _post(() =>
        {
            if (!TakeCapture(number))
            {
                return;
            }

            _ = Supersede();
            _panel.Show(null, number, result);
        });

    internal void OnMenuChosen(ConnectorSession session, MenuChooseMessage choose) =>
        _post(() =>
        {
            var menu = _menu;
            if (menu is null || menu.Session != session || menu.Id != choose.MenuId)
            {
                Trace.WriteLine($"[ActionMenu] Ignored a choice for {choose.MenuId}, which is not open.");
                return;
            }

            // The action id comes from the peer, so only a known one is traced.
            var action = RewriteActionCatalog.Find(_settings.Current, choose.Action);
            var traced = action?.Id ?? (choose.Action is MenuChoices.Review or MenuChoices.Recapture ? choose.Action : "unknown");
            if (menu.Busy is not null)
            {
                Trace.WriteLine($"[ActionMenu] Ignored {traced} on {menu.Id} while it is running.");
                return;
            }

            Trace.WriteLine($"[ActionMenu] {menu.Id} chose {traced}.");
            switch (choose.Action)
            {
                case MenuChoices.Review:
                    // The extension closed the menu itself when it sent this.
                    _menu = null;
                    _panel.Show(menu.Target, menu.Number, menu.Result);
                    break;
                case MenuChoices.Recapture:
                    _ = RecaptureAsync(menu, menu.StartBusy());
                    break;
                default:
                    if (action is not null)
                    {
                        Run(menu, action, choose.Instruction);
                    }
                    else
                    {
                        _ = SendErrorAsync(menu, _localizer["Menu.Error.UnknownAction"], canRetry: false);
                    }

                    break;
            }
        });

    internal void OnMenuClosed(ConnectorSession session, MenuCloseMessage close) =>
        _post(() =>
        {
            var menu = _menu;
            if (menu is null || menu.Session != session || menu.Id != close.MenuId)
            {
                return;
            }

            Trace.WriteLine($"[ActionMenu] {menu.Id} was closed in the browser.");
            menu.Busy?.Cancel();
            _menu = null;
        });

    private bool TakeCapture(long number)
    {
        if (number < _latestCapture)
        {
            return false;
        }

        _latestCapture = number;
        return true;
    }

    private string NextMenuId() => "menu-" + ++_menuCount;

    private bool IsCurrent(OpenMenu menu) => _menu == menu;

    /// <summary>Closes the open menu, cancelling what it was running; a failed close is ignored.</summary>
    private Task Supersede()
    {
        if (_menu is not { } menu)
        {
            return Task.CompletedTask;
        }

        _menu = null;
        menu.Busy?.Cancel();
        return SendAsync(menu, (session, ct) => session.SendMenuCloseAsync(new MenuCloseMessage { MenuId = menu.Id }, ct));
    }

    private void Show(OpenMenu menu)
    {
        _menu = menu;
        _ = ShowAsync(menu, BuildMenu(menu));
    }

    private MenuShowMessage BuildMenu(OpenMenu menu)
    {
        var settings = _settings.Current;
        var actions = RewriteActionCatalog.Resolve(settings);
        var context = menu.Capture.Context;
        var slots = s_slots.Select(id => ToConnector(actions.First(action => action.Id == id))).ToList();
        return new MenuShowMessage
        {
            MenuId = menu.Id,
            EditorId = menu.Capture.EditorId,
            Slots = slots,
            // Ask for a change is the mark itself, so it is not listed.
            More = [.. slots, .. actions.Where(action => action.Title is not null).Select(ToConnector)],
            Status =
            [
                CaptureLabels.MessageCount(context.Thread.Count, _localizer),
                CaptureLabels.Completeness(context.Completeness, context.CollapsedCount, _localizer),
                CaptureLabels.Provider(null, null, settings, _localizer),
            ],
        };
    }

    private ConnectorAction ToConnector(RewriteAction action) => new(action.Id, action.DisplayName(_localizer));

    private async Task ShowAsync(OpenMenu menu, MenuShowMessage message)
    {
        ConnectorError? error;
        try
        {
            error = await menu.Session.ShowMenuAsync(message, s_showTimeout, CancellationToken.None);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            error = ConnectorRewriteTarget.ErrorOf(ex);
        }

        if (error is null)
        {
            Trace.WriteLine($"[ActionMenu] Showing {menu.Id} with {message.More.Count} more actions.");
            _post(() =>
            {
                if (IsCurrent(menu))
                {
                    MenuShown?.Invoke();
                }
            });
            return;
        }

        _post(() =>
        {
            Trace.WriteLine($"[ActionMenu] {menu.Id} could not be shown ({ConnectorTrace.Code(error.Code)}); opening the panel.");
            if (!IsCurrent(menu))
            {
                return;
            }

            _menu = null;
            menu.Busy?.Cancel();
            if (error.Code == ConnectorRewriteTarget.TimeoutCode)
            {
                // The extension may have drawn the menu and lost only its reply; it must not stay up unowned.
                _ = SendAsync(menu, (session, ct) => session.SendMenuCloseAsync(new MenuCloseMessage { MenuId = menu.Id }, ct));
            }

            _panel.Show(menu.Target, menu.Number, menu.Result);
        });
    }

    private void Run(OpenMenu menu, RewriteAction action, string? instruction)
    {
        if (action.IsCustom && !RewriteActionCatalog.IsValidInstruction(instruction))
        {
            Trace.WriteLine($"[ActionMenu] Refused {action.Id} on {menu.Id}: instruction of {instruction?.Length ?? 0} chars.");
            _ = SendErrorAsync(
                menu,
                _localizer.Format("Menu.Error.InvalidInstruction", RewriteActionCatalog.MaxInstructionLength),
                canRetry: false
            );
            return;
        }

        var settings = _settings.Current;
        RewriteSession session;
        if (action.IsCustom && menu.Advice is { } advice)
        {
            if (!advice.UsesSameProvider(settings))
            {
                // Continuing would send the conversation to a provider the user has since switched away from. The
                // stale session stays, so a follow-up typed into the restored answer card is refused again rather
                // than run as a quick action that would replace the draft.
                Trace.WriteLine($"[ActionMenu] Refused a follow-up on {menu.Id}: the provider changed since the advice.");
                _ = SendErrorAsync(menu, _localizer["Menu.Error.ProviderChanged"], canRetry: false);
                return;
            }

            // A follow-up to What am I missing? continues that conversation and gets more advice, never a draft.
            session = advice;
        }
        else
        {
            try
            {
                session = new RewriteSession(_providers, settings, menu.Capture.Context, _builder);
            }
            catch (ProviderException ex)
            {
                Trace.WriteLine($"[ActionMenu] No provider for {action.Id} ({ex.Kind}).");
                _ = SendErrorAsync(menu, ex.Message, canRetry: true);
                return;
            }

            menu.Advice = null;
        }

        session.CaptureProvenance = settings.ProvenanceCaptureEnabled;
        _ = RunAsync(menu, menu.StartBusy(), session, action, instruction);
    }

    private async Task RunAsync(OpenMenu menu, CancellationTokenSource busy, RewriteSession session, RewriteAction action, string? instruction)
    {
        var capture = menu.Capture;
        Trace.WriteLine(
            $"[ActionMenu] Running {action.Id} on {menu.Id}, {capture.Context.Draft.Length} draft chars, "
                + $"{capture.Context.Thread.Count} messages."
        );
        _ = SendUpdateAsync(menu, new MenuUpdateMessage { MenuId = menu.Id, State = MenuStates.Running, Action = action.Id });
        RewriteResult result;
        try
        {
            result = await session.RunAsync(action, instruction, null, busy.Token);
        }
        catch (ProviderException ex)
        {
            // Local mode reports its failure here; it is never retried with another provider.
            _post(() =>
            {
                menu.EndBusy(busy);
                if (!IsCurrent(menu))
                {
                    return;
                }

                Trace.WriteLine($"[ActionMenu] {action.Id} failed on {menu.Id} ({ex.Kind}).");
                _ = SendErrorAsync(menu, ex.Message, canRetry: true);
            });
            return;
        }
        catch (OperationCanceledException)
        {
            _post(() =>
            {
                // A cancel the menu did not ask for still has to clear the running bubble.
                var unasked = !busy.IsCancellationRequested;
                menu.EndBusy(busy);
                if (unasked && IsCurrent(menu))
                {
                    _ = SendErrorAsync(menu, _localizer["Panel.Cancelled"], canRetry: true);
                }
            });
            return;
        }

        _post(() => OnRunCompleted(menu, busy, session, action, result));
    }

    private void OnRunCompleted(OpenMenu menu, CancellationTokenSource busy, RewriteSession session, RewriteAction action, RewriteResult result)
    {
        if (!IsCurrent(menu))
        {
            menu.EndBusy(busy);
            return;
        }

        Trace.WriteLine(
            $"[ActionMenu] {action.Id} done on {menu.Id}, {result.Text.Length} chars in {(int)result.Provenance.Duration.TotalMilliseconds} ms."
        );
        var historyEntryId = RecordHistory(action, menu.Capture, result);
        var isAdvice = action.IsMissing || session == menu.Advice;
        // Advice is never applicable, so a follow-up to it keeps the panel from offering the Changes view.
        var handOver = () => _panel.ShowResult(menu.Number, menu.Result, session, isAdvice ? RewriteAction.Missing : action, result);

        if (isAdvice)
        {
            menu.Advice = session;
            menu.EndBusy(busy);
            _ = SendResultAsync(menu, new MenuUpdateMessage { MenuId = menu.Id, State = MenuStates.Answer, Text = result.Text }, handOver);
            return;
        }

        if (menu.Capture.HasEmbeddedContent)
        {
            // The extension would refuse to replace a selection holding an image, so the result is offered for Copy.
            Trace.WriteLine($"[ActionMenu] Not applying to {menu.Id}: the selection holds embedded content.");
            menu.EndBusy(busy);
            _ = SendResultAsync(
                menu,
                new MenuUpdateMessage
                {
                    MenuId = menu.Id,
                    State = MenuStates.Error,
                    Text = ErrorText(UnsupportedContentCode),
                    CanRetry = false,
                    Result = result.Text,
                },
                handOver
            );
            return;
        }

        _ = ApplyAsync(menu, busy, result.Text, historyEntryId, handOver);
    }

    private async Task ApplyAsync(OpenMenu menu, CancellationTokenSource busy, string text, string? historyEntryId, Action handOver)
    {
        // Not cancellable: once apply.request is sent the extension may already have written the draft.
        var error = await menu.Target.ApplyAsync(menu.Capture, text, CancellationToken.None);
        _post(() =>
        {
            menu.EndBusy(busy);
            if (error is null && historyEntryId is not null)
            {
                WriteHistory(() => _history.MarkApplied(historyEntryId));
            }

            if (!IsCurrent(menu))
            {
                return;
            }

            if (error is null)
            {
                Trace.WriteLine($"[ActionMenu] Applied {text.Length} chars from {menu.Id}.");
                _menu = null;
                RewriteApplied?.Invoke();
                _ = SendUpdateAsync(menu, new MenuUpdateMessage { MenuId = menu.Id, State = MenuStates.Applied });
                return;
            }

            Trace.WriteLine($"[ActionMenu] Apply from {menu.Id} refused ({ConnectorTrace.Code(error.Code)}).");
            _ = SendResultAsync(
                menu,
                error.Code == DraftChangedCode
                    ? new MenuUpdateMessage { MenuId = menu.Id, State = MenuStates.DraftChanged, Text = text }
                    : new MenuUpdateMessage
                    {
                        MenuId = menu.Id,
                        State = MenuStates.Error,
                        Text = ErrorText(error.Code),
                        CanRetry = error.Code == ConnectorRewriteTarget.TimeoutCode,
                        Result = text,
                    },
                handOver
            );
        });
    }

    private async Task RecaptureAsync(OpenMenu menu, CancellationTokenSource busy)
    {
        ContextResult result;
        try
        {
            result = await menu.Target.RecaptureAsync(menu.Capture.EditorId, busy.Token);
        }
        catch (OperationCanceledException)
        {
            _post(() => menu.EndBusy(busy));
            return;
        }

        _post(() =>
        {
            menu.EndBusy(busy);
            if (!IsCurrent(menu))
            {
                return;
            }

            if (result.Capture is null)
            {
                Trace.WriteLine($"[ActionMenu] Recapture for {menu.Id} declined ({ConnectorTrace.Code(result.Error?.Code)}).");
                _ = SendErrorAsync(menu, ErrorText(result.Error?.Code), canRetry: false);
                return;
            }

            _ = Supersede();
            Show(new OpenMenu(NextMenuId(), menu.Session, menu.Target, menu.Number, result));
        });
    }

    /// <summary>Keeps the result in history when it is turned on and returns its id. A failed write never hides the result.</summary>
    private string? RecordHistory(RewriteAction action, CapturedContext capture, RewriteResult result)
    {
        var settings = _settings.Current;
        if (!settings.HistoryEnabled)
        {
            return null;
        }

        var entry = RewriteHistory.CreateEntry(action, capture, result, settings, _localizer);
        var kept = false;
        WriteHistory(() =>
        {
            _history.Add(entry);
            kept = true;
            _history.Prune(settings.HistoryRetentionDays, entry.CreatedAt);
        });
        return kept ? entry.Id : null;
    }

    private static void WriteHistory(Action write)
    {
        try
        {
            write();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"[ActionMenu] Could not save history: {ex.GetType().Name}");
        }
    }

    private string ErrorText(string? code) => _localizer[ConnectorErrorLabels.KeyFor(code)];

    private static Task<bool> SendErrorAsync(OpenMenu menu, string text, bool canRetry) =>
        SendUpdateAsync(menu, new MenuUpdateMessage { MenuId = menu.Id, State = MenuStates.Error, Text = text, CanRetry = canRetry });

    private static Task<bool> SendUpdateAsync(OpenMenu menu, MenuUpdateMessage update) =>
        SendAsync(menu, (session, ct) => session.SendMenuUpdateAsync(update, ct));

    /// <summary>
    ///     Sends an update that carries a finished result. When the browser has disconnected, the result goes
    ///     to the panel instead, so it can still be copied with history off.
    /// </summary>
    private async Task SendResultAsync(OpenMenu menu, MenuUpdateMessage update, Action handOver)
    {
        if (await SendUpdateAsync(menu, update))
        {
            return;
        }

        _post(() =>
        {
            if (!IsCurrent(menu))
            {
                return;
            }

            Trace.WriteLine($"[ActionMenu] The browser disconnected before {menu.Id} could show its result; opening the panel.");
            _menu = null;
            handOver();
        });
    }

    /// <summary>
    ///     Sends a notification to the menu's extension. A closed connection or a peer that stops reading is
    ///     traced, never thrown, and a stalled write is abandoned after a deadline so it cannot hold the channel.
    ///     Returns false when the notification was not delivered.
    /// </summary>
    private static async Task<bool> SendAsync(OpenMenu menu, Func<ConnectorSession, CancellationToken, Task> send)
    {
        using var deadline = new CancellationTokenSource(s_sendTimeout);
        try
        {
            // Exit blocks the UI thread on the close, so the continuation must not need it.
            await send(menu.Session, deadline.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            Trace.WriteLine($"[ActionMenu] Could not reach the extension for {menu.Id} ({ex.GetType().Name}).");
            return false;
        }
    }

    /// <summary>A menu the extension was asked to show, over one capture.</summary>
    private sealed class OpenMenu(string id, ConnectorSession session, IRewriteTarget target, long number, ContextResult result)
    {
        public string Id { get; } = id;
        public ConnectorSession Session { get; } = session;
        public IRewriteTarget Target { get; } = target;
        public long Number { get; } = number;
        public ContextResult Result { get; } = result;
        public CapturedContext Capture => Result.Capture!;

        /// <summary>The What am I missing? conversation that Ask for a change continues; null once another action runs.</summary>
        public RewriteSession? Advice { get; set; }

        /// <summary>Set while an action or a recapture is in flight; choices are ignored until it ends.</summary>
        public CancellationTokenSource? Busy { get; private set; }

        public CancellationTokenSource StartBusy() => Busy = new CancellationTokenSource();

        public void EndBusy(CancellationTokenSource busy)
        {
            if (Busy == busy)
            {
                Busy = null;
            }

            busy.Dispose();
        }
    }
}
