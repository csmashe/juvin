using System.Diagnostics;
using System.Net.Sockets;
using Juvin.Core.Connector;
using Juvin.Core.Hotkeys;
using Juvin.Core.Interfaces;
using Juvin.Core.Localization;
using Juvin.Core.Models;
using Juvin.Core.Rewrite;
using Juvin.Platform;

namespace Juvin.App.Services;

/// <summary>
///     Serves the browser connector endpoint: one <see cref="ConnectorSession" /> per connected
///     extension, config pushed on hello and on every settings change, and a capture requested
///     whenever the extension reports its shortcut or the desktop hotkey fires.
/// </summary>
/// <remarks>
///     Members are safe to call from any thread. Events are raised on thread-pool threads, so UI
///     subscribers must marshal to the UI thread.
/// </remarks>
public sealed class ConnectorService
{
    /// <summary>Not on the wire: the desktop hotkey fired with no browser connected.</summary>
    public const string NoBrowserCode = ConnectorErrorCodes.NoBrowser;

    public const string BrowserNotFocusedCode = "browser-not-focused";

    private static readonly IReadOnlyList<string> s_adapters = ["gmail", "generic", "test-page"];

    /// <summary>
    ///     On Windows and X11 one press fires both the global hook and the extension's command;
    ///     a capture starting this soon after the previous one is that same press.
    /// </summary>
    internal static TimeSpan CoalesceWindow { get; } = TimeSpan.FromMilliseconds(300);

    private readonly Dictionary<ConnectorSession, ConnectorConfig?> _configSends = [];
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Localizer _localizer;
    private readonly INativeHostRegistrar _registrar;
    private readonly IConnectorSocketServer _server;
    private readonly List<ConnectorSession> _sessions = [];
    private readonly ISettingsService _settings;
    private readonly TimeProvider _time;
    private long? _lastCaptureStart;
    private long _latestCapture;

    public ConnectorService(
        IConnectorSocketServer server,
        INativeHostRegistrar registrar,
        ISettingsService settings,
        Localizer localizer,
        TimeProvider? time = null
    )
    {
        _time = time ?? TimeProvider.System;
        _server = server;
        _registrar = registrar;
        _settings = settings;
        _localizer = localizer;

        _settings.SettingsChanged += _ => ResendConfig();
        _localizer.LanguageChanged += _ => ResendConfig();
    }

    public string Address => _server.Address;

    /// <summary>How long a capture waits for the extension's answer.</summary>
    internal TimeSpan CaptureTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How long one config send may wait on a peer that stops reading.</summary>
    internal TimeSpan ConfigSendTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Connected extensions that have sent <c>hello</c>, oldest first.</summary>
    public IReadOnlyList<ConnectorSession> Clients
    {
        get
        {
            lock (_gate)
            {
                return [.. _sessions.Where(session => session.Client is not null)];
            }
        }
    }

    /// <summary>Raised when an extension says hello or disconnects.</summary>
    public event Action? ClientsChanged;

    /// <summary>
    ///     Raised when a capture requested by the extension's shortcut finishes, successfully or not,
    ///     with its sequence number; a higher number is a later shortcut press.
    /// </summary>
    public event Action<ConnectorSession, long, ContextResult>? CaptureCompleted;

    /// <summary>
    ///     Raised instead of <see cref="CaptureCompleted" /> when the desktop hotkey fires with no browser
    ///     connected, so there is no session to recapture from; numbered like captures.
    /// </summary>
    public event Action<long, ContextResult>? TriggerFailed;

    /// <summary>Relays <see cref="ConnectorSession.MenuChosen" /> from every connected extension.</summary>
    public event Action<ConnectorSession, MenuChooseMessage>? MenuChosen;

    /// <summary>Relays <see cref="ConnectorSession.MenuClosed" /> from every connected extension.</summary>
    public event Action<ConnectorSession, MenuCloseMessage>? MenuClosed;

    public async Task StartAsync(CancellationToken ct)
    {
        await _server.StartAsync(HandleConnectionAsync, ct);
        await BindSandboxEndpointsAsync(ct);
    }

    /// <summary>
    ///     Listens on the registrar's sandbox endpoints too. Called on start and again after a
    ///     registration, which can add one. Returns the addresses that could not be bound.
    /// </summary>
    public async Task<IReadOnlyList<SandboxEndpoint>> BindSandboxEndpointsAsync(CancellationToken ct)
    {
        var failed = new List<SandboxEndpoint>();
        foreach (var endpoint in _registrar.SandboxEndpoints)
        {
            try
            {
                await _server.AddAddressAsync(endpoint.Address, ct);
            }
            catch (Exception ex)
                when (ex
                    is IOException
                        or SocketException
                        or ArgumentException
                        or NotSupportedException
                        or UnauthorizedAccessException
                        or InvalidOperationException
                )
            {
                Trace.WriteLine($"[ConnectorService] Could not listen on {endpoint.Address} ({ex.GetType().Name}).");
                failed.Add(endpoint);
            }
        }

        return failed;
    }

    public async Task StopAsync()
    {
        // Exit blocks the UI thread on this call, so nothing here may need that thread to resume.
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _server.StopAsync().ConfigureAwait(false);
    }

    public ConnectorConfig BuildConfig(AppSettings settings)
    {
        var shortcut = KeyChord.TryParse(settings.RewriteShortcut, out var saved)
            ? saved.WithPhysicalKey(settings.RewriteShortcutPhysicalKey)
            : HotkeyService.EffectiveShortcut(settings.RewriteShortcut);
        return new ConnectorConfig
        {
            Actions = [.. RewriteActionCatalog.Resolve(settings).Select(action => new ConnectorAction(action.Id, action.DisplayName(_localizer)))],
            Adapters = s_adapters,
            Shortcut = shortcut.ToString(),
            ShortcutKey = shortcut.PhysicalKey == shortcut.Key ? null : shortcut.PhysicalKey,
            Health = new ConnectorHealth(settings.ProcessingMode == ProcessingMode.Codex ? "codex" : "local", null),
        };
    }

    /// <summary>Round-trip time to the most recently connected extension, or null when none answers.</summary>
    public async Task<TimeSpan?> PingAsync(TimeSpan timeout, CancellationToken ct)
    {
        var clients = Clients;
        var session = clients.Count > 0 ? clients[^1] : null;
        if (session is null)
        {
            return null;
        }

        try
        {
            return await session.PingAsync(timeout, ct);
        }
        catch (Exception ex) when (ex is TimeoutException or ConnectorException)
        {
            Trace.WriteLine($"[ConnectorService] Ping failed ({ex.GetType().Name}).");
            return null;
        }
    }

    /// <summary>Asks the most recently connected extension to open its sample page; null on success, <c>no-browser</c> with none connected. Never throws.</summary>
    public async Task<ConnectorError?> OpenTestPageAsync(TimeSpan timeout, CancellationToken ct)
    {
        var clients = Clients;
        if (clients.Count == 0)
        {
            return new ConnectorError(NoBrowserCode, _localizer["Panel.Error.NoBrowser"]);
        }

        try
        {
            return await clients[^1].OpenTestPageAsync(timeout, ct);
        }
        catch (Exception ex) when (ex is TimeoutException or ConnectorException)
        {
            Trace.WriteLine($"[ConnectorService] Opening the sample page failed ({ex.GetType().Name}).");
            return ConnectorRewriteTarget.ErrorOf(ex);
        }
    }

    /// <summary>
    ///     Captures for the desktop hotkey, which has no originating browser: asks every connected
    ///     extension at once and reports the first capture, else an error other than
    ///     <c>browser-not-focused</c> when one exists. Never throws.
    /// </summary>
    public async Task TriggerCaptureAsync()
    {
        if (!TryStartCapture(out var number))
        {
            return;
        }

        var clients = Clients;
        if (clients.Count == 0)
        {
            Trace.WriteLine("[ConnectorService] Hotkey capture with no browser connected.");
            ReleaseCapture();
            var error = new ContextResult(null, new ConnectorError(NoBrowserCode, _localizer["Panel.Error.NoBrowser"]));
            if (IsLatest(number))
            {
                Raise(() => TriggerFailed?.Invoke(number, error));
            }

            return;
        }

        var pending = clients.ToDictionary(RequestFocusedAsync, session => session);
        (ConnectorSession Session, ContextResult Result)? chosen = null;
        while (pending.Count > 0)
        {
            var finished = await Task.WhenAny(pending.Keys);
            var session = pending[finished];
            pending.Remove(finished);
            var result = await finished;
            if (result.Ok)
            {
                chosen = (session, result);
                break;
            }

            if (chosen is null || Rank(result) > Rank(chosen.Value.Result))
            {
                chosen = (session, result);
            }
        }

        Trace.WriteLine($"[ConnectorService] Hotkey capture asked {clients.Count} browsers.");
        if (chosen is not { } winner)
        {
            ReleaseCapture();
            return;
        }

        Report(winner.Session, number, winner.Result);
    }

    /// <summary>
    ///     True when a browser answered itself; a timeout, a broken frame, or a closed connection is the app's
    ///     own verdict and says nothing about which browser has focus.
    /// </summary>
    internal static bool AnsweredByBrowser(ContextResult result) =>
        result.Ok
        || result.Error?.Code
            is not (
                ConnectorRewriteTarget.TimeoutCode
                or ConnectorRewriteTarget.TooLargeCode
                or ConnectorRewriteTarget.FailedCode
                or ConnectorErrorCodes.Disconnected
                or NoBrowserCode
            );

    /// <summary>
    ///     A capture, then a browser's own refusal, then a timeout (likely the focused browser hung), then
    ///     "not focused", then the app's other local failures.
    /// </summary>
    private static int Rank(ContextResult result) =>
        result.Ok ? 4
        : result.Error?.Code == ConnectorRewriteTarget.TimeoutCode ? 2
        : !AnsweredByBrowser(result) ? 0
        : result.Error?.Code == BrowserNotFocusedCode ? 1
        : 3;

    private async Task HandleConnectionAsync(Stream stream, CancellationToken ct)
    {
        var session = new ConnectorSession(new StreamConnectorChannel(stream), () => BuildConfig(_settings.Current));
        session.HelloReceived += connected =>
        {
            var client = connected.Client!;
            Trace.WriteLine($"[ConnectorService] {ConnectorTrace.BrowserName(client.Browser.Name)} {ConnectorTrace.Version(client.Browser.Version)} connected (extension {ConnectorTrace.Version(client.Extension.Version)}).");
            ClientsChanged?.Invoke();
        };
        session.ShortcutPressed += pressed => _ = CaptureAsync(pressed);
        session.MenuChosen += (sender, choose) => MenuChosen?.Invoke(sender, choose);
        session.MenuClosed += (sender, close) => MenuClosed?.Invoke(sender, close);
        lock (_gate)
        {
            _sessions.Add(session);
        }

        try
        {
            await session.RunAsync(ct);
        }
        finally
        {
            bool wasClient;
            lock (_gate)
            {
                _sessions.Remove(session);
                wasClient = session.Client is not null;
            }

            if (wasClient)
            {
                Trace.WriteLine($"[ConnectorService] {ConnectorTrace.BrowserName(session.Client!.Browser.Name)} disconnected.");
                ClientsChanged?.Invoke();
            }
        }
    }

    private async Task CaptureAsync(ConnectorSession session)
    {
        if (!TryStartCapture(out var number))
        {
            return;
        }

        ContextResult result;
        try
        {
            result = await session.RequestContextAsync(null, CaptureTimeout, CancellationToken.None);
        }
        catch (Exception ex) when (ex is TimeoutException or ConnectorException)
        {
            Trace.WriteLine($"[ConnectorService] Capture failed ({ex.GetType().Name}).");
            result = new ContextResult(null, ConnectorRewriteTarget.ErrorOf(ex));
        }

        Report(session, number, result);
    }

    private async Task<ContextResult> RequestFocusedAsync(ConnectorSession session)
    {
        try
        {
            return await session.RequestFocusedContextAsync(CaptureTimeout, CancellationToken.None);
        }
        catch (Exception ex) when (ex is TimeoutException or ConnectorException)
        {
            Trace.WriteLine($"[ConnectorService] Hotkey capture failed in one browser ({ex.GetType().Name}).");
            // A closed or broken session is the app's own verdict; it must not rank as a browser's answer.
            var error = ConnectorRewriteTarget.ErrorOf(ex);
            return new ContextResult(null, ex is TimeoutException ? error : error with { Code = ConnectorRewriteTarget.FailedCode });
        }
    }

    /// <summary>Forgets the last start so a press the extension reports right after a no-browser trigger still captures.</summary>
    private void ReleaseCapture()
    {
        lock (_gate)
        {
            _lastCaptureStart = null;
        }
    }

    /// <summary>Numbers a new capture, or returns false when it repeats the press that started the previous one.</summary>
    private bool TryStartCapture(out long number)
    {
        var now = _time.GetTimestamp();
        lock (_gate)
        {
            if (_lastCaptureStart is { } last && _time.GetElapsedTime(last, now) < CoalesceWindow)
            {
                number = 0;
                Trace.WriteLine("[ConnectorService] Coalesced duplicate press.");
                return false;
            }

            _lastCaptureStart = now;
        }

        number = Interlocked.Increment(ref _latestCapture);
        return true;
    }

    private bool IsLatest(long number)
    {
        // A slow capture (Gmail expanding a thread) must not replace one from a later shortcut press.
        var latest = Interlocked.Read(ref _latestCapture);
        if (number == latest)
        {
            return true;
        }

        Trace.WriteLine($"[ConnectorService] Discarded capture {number}; capture {latest} is newer.");
        return false;
    }

    private void Report(ConnectorSession session, long number, ContextResult result)
    {
        if (!IsLatest(number))
        {
            return;
        }

        if (result.Capture is { } capture)
        {
            // Lengths and counts only: the draft and thread are private.
            Trace.WriteLine(
                $"[ConnectorService] Captured {capture.Context.Draft.Length} draft chars, "
                    + $"{capture.Context.Thread.Count} messages, {capture.Context.Completeness}, adapter {(s_adapters.Contains(capture.Adapter) ? capture.Adapter : "unknown")}."
            );
        }
        else
        {
            Trace.WriteLine($"[ConnectorService] Capture declined ({ConnectorTrace.Code(result.Error?.Code)}).");
        }

        Raise(() => CaptureCompleted?.Invoke(session, number, result));
    }

    private static void Raise(Action raise)
    {
        try
        {
            raise();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ConnectorService] A capture handler threw {ex.GetType().Name}.");
        }
    }

    private void ResendConfig()
    {
        var config = BuildConfig(_settings.Current);
        foreach (var session in Clients)
        {
            _ = QueueConfigAsync(session, config);
        }
    }

    /// <summary>
    ///     Sends <paramref name="config" />, or while a send to this session is still pending, keeps it as the one
    ///     to send next; a later call replaces it, so a slow reader gets only the latest config.
    /// </summary>
    internal async Task QueueConfigAsync(ConnectorSession session, ConnectorConfig config)
    {
        lock (_gate)
        {
            if (!_configSends.TryAdd(session, null))
            {
                _configSends[session] = config;
                return;
            }
        }

        while (true)
        {
            await SendConfigAsync(session, config);
            lock (_gate)
            {
                if (_configSends[session] is not { } next)
                {
                    _configSends.Remove(session);
                    return;
                }

                config = next;
                _configSends[session] = null;
            }
        }
    }

    private async Task SendConfigAsync(ConnectorSession session, ConnectorConfig config)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        deadline.CancelAfter(ConfigSendTimeout);
        try
        {
            await session.SendConfigAsync(config, deadline.Token);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ConnectorException)
        {
            Trace.WriteLine($"[ConnectorService] Could not resend config ({ex.GetType().Name}).");
        }
    }
}
