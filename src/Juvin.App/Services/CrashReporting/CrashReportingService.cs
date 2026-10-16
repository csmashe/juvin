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
                StripReservedData(exception, 0);
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

    private void OnSettingsChanged(AppSettings settings)
    {
        lock (_lock)
        {
            if (!_disposed && !_shutdown)
            {
                Apply(settings);
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
                    var gate = new CrashReportConsentGate(_innerHandler ?? new HttpClientHandler());
                    _gate = gate;
                    _sdk = SentrySdk.Init(options =>
                    {
                        SentryOptionsFactory.Configure(options, _info, gate);
                        _configure?.Invoke(options);
                    });
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

    // The SDK promotes "sentry:context:*" and "sentry:tag:*" data entries into the event before BeforeSend runs.
    private static void StripReservedData(Exception? exception, int depth)
    {
        while (exception is not null && depth <= 32)
        {
            foreach (var key in exception.Data.Keys.OfType<string>().Where(key => key.StartsWith("sentry:", StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                exception.Data.Remove(key);
            }

            if (exception is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    StripReservedData(inner, depth + 1);
                }

                return;
            }

            exception = exception.InnerException;
            depth++;
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
