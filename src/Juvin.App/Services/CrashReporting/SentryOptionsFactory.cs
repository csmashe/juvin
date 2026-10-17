using Sentry.Extensibility;

namespace Juvin.App.Services.CrashReporting;

internal static class SentryOptionsFactory
{
    public static void Configure(SentryOptions options, CrashReportBuildInfo info, CrashReportConsentGate gate)
    {
        options.Dsn = info.Dsn;
        options.Release = info.Release;
        options.Environment = info.Environment;
        options.SendDefaultPii = false;
        options.IsEnvironmentUser = false;
        options.ServerName = null;
        options.IsGlobalModeEnabled = true;
        options.AutoSessionTracking = false;
        options.TracesSampleRate = 0;
        options.CaptureFailedRequests = false;
        options.DisableSentryHttpMessageHandler = true;
        options.SendClientReports = false;
        options.EnableLogs = false;
        options.AttachStacktrace = true;
        options.MaxBreadcrumbs = 0;
        options.ReportAssembliesMode = ReportAssembliesMode.None;
        options.CacheDirectoryPath = null;
        options.DisableFileWrite = true;
        options.ShutdownTimeout = TimeSpan.FromSeconds(2);
        options.CreateHttpMessageHandler = () => gate;
        options.DisableDiagnosticSourceIntegration();
        // Program and App capture these themselves, so each crash has one path and an operation tag.
        options.DisableAppDomainUnhandledExceptionCapture();
        options.DisableUnobservedTaskExceptionCapture();
        options.AddExceptionFilterForType<OperationCanceledException>();
        options.AddExceptionFilter(new CancelledAggregateFilter());
        options.SetBeforeSend(CrashReportScrubber.Scrub);
    }
}

/// <summary>Drops the <see cref="AggregateException" /> an unobserved cancelled task raises, which the type filter cannot see.</summary>
internal sealed class CancelledAggregateFilter : IExceptionFilter
{
    public bool Filter(Exception ex)
    {
        return ex is AggregateException aggregate
            && aggregate.Flatten().InnerExceptions is { Count: > 0 } inners
            && inners.All(inner => inner is OperationCanceledException);
    }
}
