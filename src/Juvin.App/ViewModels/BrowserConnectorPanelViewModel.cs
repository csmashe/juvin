using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentIcons.Common;
using Juvin.App.Services;
using Juvin.Core.Localization;
using Juvin.Platform;

namespace Juvin.App.ViewModels;

/// <summary>
///     Browser registration, extension install hint, and connection test. Shared by the Browser
///     connector section and the wizard's browser step.
/// </summary>
public sealed partial class BrowserConnectorPanelViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan s_pingTimeout = TimeSpan.FromSeconds(5);

    private readonly ConnectorService _connector;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Localizer _localizer;
    private readonly INativeHostRegistrar _registrar;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHostMissing))]
    private string? _hostPath;

    [ObservableProperty]
    private bool _isTesting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTestResult))]
    private string? _testResult;

    public BrowserConnectorPanelViewModel(INativeHostRegistrar registrar, ConnectorService connector, Localizer localizer)
    {
        _registrar = registrar;
        _connector = connector;
        _localizer = localizer;
        HostMissingHint = localizer.Format("BrowserConnector.HostMissingHint", NativeHost.PathOverrideVariable);
        _connector.ClientsChanged += OnClientsChanged;
        RefreshClients();
    }

    public ObservableCollection<BrowserRow> Browsers { get; } = [];
    public ObservableCollection<string> ConnectedClients { get; } = [];
    public string HostMissingHint { get; }

    public bool HasBrowsers => Browsers.Count > 0;
    public bool IsHostMissing => HostPath is null;
    public bool HasClients => ConnectedClients.Count > 0;
    public bool HasTestResult => !string.IsNullOrWhiteSpace(TestResult);

    private bool IsDisposed => _lifetime.IsCancellationRequested;

    public void Dispose()
    {
        _connector.ClientsChanged -= OnClientsChanged;
        if (!_lifetime.IsCancellationRequested)
        {
            _lifetime.Cancel();
        }

        _lifetime.Dispose();
    }

    [RelayCommand]
    private async Task DetectAsync()
    {
        if (IsDisposed)
        {
            return;
        }

        HostPath = NativeHost.Locate();
        var targets = _registrar.DetectBrowsers();
        var busy = Browsers.Where(row => row.IsBusy).ToDictionary(row => row.Target.Id);
        Browsers.Clear();
        foreach (var target in targets)
        {
            // A row mid-registration keeps reporting its own outcome instead of a stale check.
            Browsers.Add(
                busy.TryGetValue(target.Id, out var row)
                    ? row
                    : new BrowserRow(target, _registrar.CheckRegistration(target), _localizer)
            );
        }

        OnPropertyChanged(nameof(HasBrowsers));
        await BindSandboxEndpointsAsync();
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RegisterAsync(BrowserRow? row)
    {
        if (row is null || row.IsBusy || IsDisposed)
        {
            return;
        }

        row.IsBusy = true;
        row.Error = null;
        try
        {
            var token = _lifetime.Token;
            await Task.Run(() => _registrar.RegisterAsync(row.Target, token), token);
        }
        catch (OperationCanceledException) when (IsDisposed)
        {
            return;
        }
        catch (NativeHostRegistrationException ex)
        {
            Trace.WriteLine($"[BrowserConnector] Registering {row.Target.Id} failed.");
            row.Error = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"[BrowserConnector] Registering {row.Target.Id} failed ({ex.GetType().Name}).");
            row.Error = _localizer.Format("BrowserConnector.RegisterFailed", ex.Message);
        }
        finally
        {
            row.IsBusy = false;
        }

        if (IsDisposed)
        {
            return;
        }

        row.State = _registrar.CheckRegistration(row.Target);
        if (row.State == HostRegistration.Registered)
        {
            await BindSandboxEndpointsAsync();
        }
    }

    /// <summary>
    ///     A sandboxed browser reaches the app only through its own socket, so a socket that could
    ///     not be opened is shown on that browser's row. Runs on every detection so the user can retry.
    /// </summary>
    private async Task BindSandboxEndpointsAsync()
    {
        if (_registrar.SandboxEndpoints.Count == 0)
        {
            return;
        }

        var unbound = await _connector.BindSandboxEndpointsAsync(CancellationToken.None);
        if (IsDisposed)
        {
            return;
        }

        foreach (var endpoint in unbound)
        {
            var row = Browsers.FirstOrDefault(candidate => candidate.Target.Id == endpoint.BrowserId);
            row?.Error = _localizer.Format("BrowserConnector.SandboxSocketFailed", endpoint.Address);
        }
    }

    [RelayCommand]
    private async Task TestAsync()
    {
        if (IsDisposed)
        {
            return;
        }

        if (_connector.Clients.Count == 0)
        {
            TestResult = _localizer["BrowserConnector.NoExtension"];
            return;
        }

        IsTesting = true;
        TestResult = null;
        TimeSpan? roundTrip;
        try
        {
            roundTrip = await _connector.PingAsync(s_pingTimeout, _lifetime.Token);
        }
        catch (OperationCanceledException) when (IsDisposed)
        {
            return;
        }
        finally
        {
            if (!IsDisposed)
            {
                IsTesting = false;
            }
        }

        if (IsDisposed)
        {
            return;
        }

        TestResult = roundTrip is { } elapsed
            ? _localizer.Format("BrowserConnector.RoundTrip", (int)Math.Ceiling(elapsed.TotalMilliseconds))
            : _localizer["BrowserConnector.NoAnswer"];
    }

    private void OnClientsChanged() => Dispatcher.UIThread.Post(RefreshClients);

    private void RefreshClients()
    {
        if (IsDisposed)
        {
            return;
        }

        ConnectedClients.Clear();
        foreach (var client in _connector.Clients.Select(session => session.Client!))
        {
            ConnectedClients.Add(
                _localizer.Format(
                    "BrowserConnector.ConnectedClient",
                    BrowserLabels.Describe(client.Browser.Name, _localizer),
                    client.Browser.Version,
                    client.Extension.Version
                )
            );
        }

        OnPropertyChanged(nameof(HasClients));
    }
}

/// <summary>One detected browser and whether the native host is registered with it.</summary>
public sealed partial class BrowserRow : ObservableObject
{
    private readonly Localizer _localizer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRegister))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(StateText),
        nameof(RegisterLabel),
        nameof(CanRegister),
        nameof(IsSatisfied),
        nameof(NeedsAttention),
        nameof(StatusSymbol)
    )]
    private HostRegistration _state;

    public BrowserRow(BrowserTarget target, HostRegistration state, Localizer localizer)
    {
        _localizer = localizer;
        Target = target;
        _state = state;
        PackagingLabel = target.Packaging switch
        {
            BrowserPackaging.Flatpak => localizer["BrowserConnector.Packaging.Flatpak"],
            BrowserPackaging.Snap => localizer["BrowserConnector.Packaging.Snap"],
            _ => null,
        };
    }

    public BrowserTarget Target { get; }
    public string DisplayName => Target.DisplayName;
    public string? PackagingLabel { get; }
    public bool HasPackaging => PackagingLabel is not null;
    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    public string StateText =>
        _localizer[
            State switch
            {
                HostRegistration.Registered => "BrowserConnector.State.Registered",
                HostRegistration.Stale => "BrowserConnector.State.Stale",
                _ => "BrowserConnector.State.NotRegistered",
            }
        ];

    public string RegisterLabel => _localizer[State == HostRegistration.Stale ? "BrowserConnector.Update" : "BrowserConnector.Register"];
    public bool CanRegister => !IsBusy && State != HostRegistration.Registered;
    public bool IsSatisfied => State == HostRegistration.Registered;
    public bool NeedsAttention => State == HostRegistration.Stale;

    public Symbol StatusSymbol =>
        State switch
        {
            HostRegistration.Registered => Symbol.CheckmarkCircle,
            HostRegistration.Stale => Symbol.Warning,
            _ => Symbol.DismissCircle,
        };
}
