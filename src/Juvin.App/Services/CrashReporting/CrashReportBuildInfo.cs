using System.Reflection;

namespace Juvin.App.Services.CrashReporting;

/// <param name="Dsn">
///     The endpoint, or null when there is none. Builds set it with <c>-p:JuvinSentryDsn=...</c>; the
///     <c>JUVIN_SENTRY_DSN</c> environment variable overrides it so a developer can send a test report from any build.
/// </param>
/// <param name="Release">"juvin@" and the app version.</param>
/// <param name="Environment">"development" for Debug builds, "production" otherwise.</param>
public sealed record CrashReportBuildInfo(string? Dsn, string Release, string Environment)
{
    public static CrashReportBuildInfo Current()
    {
        var dsn = System.Environment.GetEnvironmentVariable("JUVIN_SENTRY_DSN");
        if (string.IsNullOrWhiteSpace(dsn))
        {
            dsn = typeof(CrashReportBuildInfo)
                .Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(attribute => attribute.Key == "SentryDsn")
                ?.Value;
        }

        return new CrashReportBuildInfo(
            string.IsNullOrWhiteSpace(dsn) ? null : dsn.Trim(),
            "juvin@" + AppVersion.Current,
#if DEBUG
            "development"
#else
            "production"
#endif
        );
    }
}
