using Juvin.Platform;

namespace Juvin.App.Tests.Fakes;

/// <summary>A registrar over an in-memory list of browsers. Registering marks the browser registered unless <see cref="RegisterFailure" /> is set.</summary>
public sealed class FakeNativeHostRegistrar : INativeHostRegistrar
{
    public List<BrowserTarget> Browsers { get; } = [];
    public Dictionary<string, HostRegistration> States { get; } = [];
    public List<SandboxEndpoint> Endpoints { get; } = [];

    /// <summary>When set, <see cref="RegisterAsync" /> throws a <see cref="NativeHostRegistrationException" /> with this message.</summary>
    public string? RegisterFailure { get; set; }

    /// <summary>When set, <see cref="RegisterAsync" /> waits for it before completing.</summary>
    public TaskCompletionSource? RegisterGate { get; set; }

    /// <summary>Endpoint address a successful registration adds for that browser, like a Flatpak socket.</summary>
    public string? EndpointAddedOnRegister { get; set; }

    public int DetectCount { get; private set; }

    public IReadOnlyList<SandboxEndpoint> SandboxEndpoints => Endpoints;

    public IReadOnlyList<BrowserTarget> DetectBrowsers()
    {
        DetectCount++;
        return [.. Browsers];
    }

    public HostRegistration CheckRegistration(BrowserTarget browser) =>
        States.GetValueOrDefault(browser.Id, HostRegistration.NotRegistered);

    public async Task RegisterAsync(BrowserTarget browser, CancellationToken ct)
    {
        if (RegisterGate is not null)
        {
            await RegisterGate.Task;
        }

        if (RegisterFailure is not null)
        {
            throw new NativeHostRegistrationException(RegisterFailure);
        }

        States[browser.Id] = HostRegistration.Registered;
        if (EndpointAddedOnRegister is not null)
        {
            Endpoints.Add(new SandboxEndpoint(browser.Id, EndpointAddedOnRegister));
        }
    }

    public Task UnregisterAsync(BrowserTarget browser, CancellationToken ct)
    {
        States.Remove(browser.Id);
        return Task.CompletedTask;
    }
}
