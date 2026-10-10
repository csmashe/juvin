using System.Diagnostics;
using System.Net.Sockets;
using Juvin.Core.Connector;
using Juvin.Core.Interfaces;
using Juvin.Core.Localization;
using Juvin.Core.Models;
using Juvin.Core.Rewrite;
using Juvin.Platform;

namespace Juvin.App.Services;

/// <summary>
///     Serves the browser connector endpoint: one <see cref="ConnectorSession" /> per connected
///     extension, config pushed on hello and on every settings change, and a capture requested
///     whenever the extension reports its shortcut.
/// </summary>
/// <remarks>
///     Members are safe to call from any thread. Events are raised on thread-pool threads, so UI
///     subscribers must marshal to the UI thread.
/// </remarks>
public sealed class ConnectorService
{
    private static readonly IReadOnlyList<string> s_adapters = ["gmail", "generic", "test-page"];

    private static readonly TimeSpan s_captureTimeout = TimeSpan.FromSeconds(10);

    private readonly Lock _gate = new();
    private readonly Localizer _localizer;
    private readonly INativeHostRegistrar _registrar;
    private readonly IConnectorSocketServer _server;
    private readonly List<ConnectorSession> _sessions = [];
    private readonly ISettingsService _settings;

    public ConnectorService(
        IConnectorSocketServer server,
        INativeHostRegistrar registrar,
        ISettingsService settings,
        Localizer localizer
    )
    {
        _server = server;
        _registrar = registrar;
        _settings = settings;
        _localizer = localizer;

        _settings.SettingsChanged += _ => ResendConfig();
        _localizer.LanguageChanged += _ => ResendConfig();
    }

    public string Address => _server.Address;

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

    /// <summary>Raised when a capture requested by the extension's shortcut finishes, successfully or not.</summary>
    public event Action<ConnectorSession, ContextResult>? CaptureCompleted;

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

    public Task StopAsync() => _server.StopAsync();

    public ConnectorConfig BuildConfig(AppSettings settings) =>
        new()
        {
            Actions = [.. RewriteAction.BuiltIn.Select(action => new ConnectorAction(action.Id, _localizer[action.TitleKey]))],
            Adapters = s_adapters,
            Shortcut = settings.RewriteShortcut,
            Health = new ConnectorHealth(settings.ProcessingMode == ProcessingMode.Codex ? "codex" : "local", null),
        };

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

    private async Task HandleConnectionAsync(Stream stream, CancellationToken ct)
    {
        var session = new ConnectorSession(new StreamConnectorChannel(stream), () => BuildConfig(_settings.Current));
        session.HelloReceived += connected =>
        {
            var client = connected.Client!;
            Trace.WriteLine($"[ConnectorService] {client.Browser.Name} {client.Browser.Version} connected (extension {client.Extension.Version}).");
            ClientsChanged?.Invoke();
        };
        session.ShortcutPressed += pressed => _ = CaptureAsync(pressed);
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
                Trace.WriteLine($"[ConnectorService] {session.Client!.Browser.Name} disconnected.");
                ClientsChanged?.Invoke();
            }
        }
    }

    private async Task CaptureAsync(ConnectorSession session)
    {
        ContextResult result;
        try
        {
            result = await session.RequestContextAsync(s_captureTimeout, CancellationToken.None);
        }
        catch (Exception ex) when (ex is TimeoutException or ConnectorException)
        {
            Trace.WriteLine($"[ConnectorService] Capture failed ({ex.GetType().Name}).");
            return;
        }

        if (result.Capture is { } capture)
        {
            // Lengths and counts only: the draft and thread are private.
            Trace.WriteLine(
                $"[ConnectorService] Captured {capture.Context.Draft.Length} draft chars, "
                    + $"{capture.Context.Thread.Count} messages, {capture.Context.Completeness}, adapter {capture.Adapter}."
            );
        }
        else
        {
            Trace.WriteLine($"[ConnectorService] Capture declined ({result.Error?.Code}).");
        }

        try
        {
            CaptureCompleted?.Invoke(session, result);
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
            _ = SendConfigAsync(session, config);
        }
    }

    private static async Task SendConfigAsync(ConnectorSession session, ConnectorConfig config)
    {
        try
        {
            await session.SendConfigAsync(config, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException)
        {
            Trace.WriteLine($"[ConnectorService] Could not resend config ({ex.GetType().Name}).");
        }
    }
}
