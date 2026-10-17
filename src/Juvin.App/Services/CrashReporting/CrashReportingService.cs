using System.Diagnostics;
using Juvin.Core.Interfaces;
using Juvin.Core.Models;

namespace Juvin.App.Services.CrashReporting;

/// <summary>
///     Runs the Sentry SDK only while <see cref="AppSettings.CrashReportingEnabled" /> is on and onboarding is complete,
///     so nothing is sent before the user has seen the choice in the setup wizard. Turning it off closes
///     the consent gate before the SDK is disposed, so reports still queued are dropped rather than sent.
/// </summary>
public sealed class CrashReportingService : ICrashReporter, IDisposable
{
    private readonly Action<SentryOptions>? _configure;
    private readonly CrashReportBuildInfo _info;
    private readonly HttpMessageHandler? _innerHandler;
    private readonly Lock _lock = new();
    private readonly ISettingsService _settings;
    private bool _disposed;
    private CrashReportConsentGate? _gate;
    private IDisposable? _sdk;
    private bool _shutdown;
    private bool _started;

    public CrashReportingService(ISettingsService settings)
        : this(settings, CrashReportBuildInfo.Current(), null, null)
    {
    }

    /// <param name="settings">The settings whose crash reporting switch turns the SDK on and off.</param>
    /// <param name="info">The endpoint, release, and environment.</param>
    /// <param name="innerHandler">Where the gate forwards reports; tests pass a recording handler.</param>
    /// <param name="configure">Runs after the shared options; tests use it to shorten timeouts.</param>
    internal CrashReportingService(
        ISettingsService settings,
        CrashReportBuildInfo info,
        HttpMessageHandler? innerHandler,
        Action<SentryOptions>? configure
    )
    {
        _settings = settings;
        _info = info;
        _innerHandler = innerHandler;
        _configure = configure;
    }

    public bool IsAvailable => _info.Dsn is not null;

    public bool IsEnabled
    {
        get
        {
            lock (_lock)
            {
                return _sdk is not null;
            }
        }
    }

    internal CrashReportConsentGate? ConsentGate
    {
        get
        {
            lock (_lock)
            {
                return _gate;
            }
        }
    }

    public void CaptureException(Exception exception, string operation, bool fatal = false)
    {
        lock (_lock)
        {
            if (_sdk is null)
            {
                return;
            }

            try
            {
                StripReservedData(exception);
                exception.SetSentryMechanism(operation, handled: !fatal, terminal: fatal);
                SentrySdk.CaptureException(exception, scope => scope.SetTag(CrashReportScrubber.OperationTag, operation));
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[CrashReporting] Capture failed: {ex.GetType().Name}");
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _settings.SettingsChanged -= OnSettingsChanged;
            Shutdown();
        }
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_started || _disposed || _shutdown)
            {
                return;
            }

            _started = true;
            _settings.SettingsChanged += OnSettingsChanged;
            Apply(_settings.Current);
        }
    }

    /// <summary>Waits for queued reports to leave, for a crash that is about to end the process.</summary>
    public void Flush(TimeSpan timeout)
    {
        if (!IsEnabled)
        {
            return;
        }

        try
        {
            SentrySdk.Flush(timeout);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[CrashReporting] Flush failed: {ex.GetType().Name}");
        }
    }

    public void Shutdown()
    {
        lock (_lock)
        {
            _shutdown = true;
            CloseSdk();
        }
    }

    // Reads the current settings rather than the notification's snapshot, so a stale enabled notification
    // delivered after a newer disabled one cannot restart the SDK.
    private void OnSettingsChanged(AppSettings _)
    {
        lock (_lock)
        {
            if (!_disposed && !_shutdown)
            {
                Apply(_settings.Current);
            }
        }
    }

    private void Apply(AppSettings settings)
    {
        var enabled = settings is { CrashReportingEnabled: true, HasCompletedOnboarding: true };
        try
        {
            switch (enabled)
            {
                case true when _sdk is null && IsAvailable:
                    var gate = new CrashReportConsentGate(_innerHandler ?? new HttpClientHandler(), disposeInner: _innerHandler is null);
                    try
                    {
                        // ReSharper disable once AccessToDisposedClosure -- the options callback runs inside Init, before the catch could dispose the gate
                        _sdk = SentrySdk.Init(options =>
                        {
                            SentryOptionsFactory.Configure(options, _info, gate);
                            _configure?.Invoke(options);
                        });
                    }
                    catch
                    {
                        gate.Dispose();
                        throw;
                    }

                    _gate = gate;
                    break;
                case false when _sdk is not null:
                    _gate?.Close();
                    CloseSdk();
                    break;
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[CrashReporting] Configuration failed: {ex.GetType().Name}");
        }
    }

    // The SDK promotes "sentry:context:*" and "sentry:tag:*" data entries into the event before BeforeSend runs,
    // at any depth, so the whole graph is walked; the visited set guards against a cycle.
    internal static void StripReservedData(Exception exception)
    {
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<Exception>();
        pending.Push(exception);
        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current))
            {
                continue;
            }

            foreach (var key in current.Data.Keys.OfType<string>().Where(key => key.StartsWith("sentry:", StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                current.Data.Remove(key);
            }

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    pending.Push(inner);
                }
            }
            else if (current.InnerException is { } inner)
            {
                pending.Push(inner);
            }
        }
    }

    private void CloseSdk()
    {
        var sdk = _sdk;
        _sdk = null;
        try
        {
            sdk?.Dispose();
        }
        finally
        {
            _gate?.Dispose();
        }
    }
}
