using Juvin.App.Services.CrashReporting;

namespace Juvin.App.Tests.CrashReporting;

[Collection(SentrySdkCollection.Name)]
public sealed class SentryOptionsFactoryTests
{
    [Fact]
    public void Configure_SetsTheBuildAndPrivacyOptions()
    {
        var info = new CrashReportBuildInfo("https://public@example.invalid/1", "juvin@test", "test");
        var options = new SentryOptions();
        using var gate = new CrashReportConsentGate(new HttpClientHandler());

        SentryOptionsFactory.Configure(options, info, gate);

        Assert.Same(gate, options.CreateHttpMessageHandler!());
        Assert.Equal(info.Dsn, options.Dsn);
        Assert.Equal("juvin@test", options.Release);
        Assert.Equal("test", options.Environment);
        Assert.False(options.SendDefaultPii);
        Assert.False(options.IsEnvironmentUser);
        Assert.Null(options.ServerName);
        Assert.Equal(0, options.MaxBreadcrumbs);
        Assert.Equal(0.0, options.TracesSampleRate);
        Assert.False(options.AutoSessionTracking);
        Assert.False(options.CaptureFailedRequests);
        Assert.False(options.SendClientReports);
        Assert.False(options.EnableLogs);
        Assert.True(options.DisableFileWrite);
        Assert.Null(options.CacheDirectoryPath);
        Assert.Equal(ReportAssembliesMode.None, options.ReportAssembliesMode);
    }

    [Fact]
    public void BuildInfo_TakesTheEnvironmentOverride()
    {
        var previous = Environment.GetEnvironmentVariable("JUVIN_SENTRY_DSN");
        try
        {
            Environment.SetEnvironmentVariable("JUVIN_SENTRY_DSN", " https://public@example.invalid/2 ");
            var info = CrashReportBuildInfo.Current();

            Assert.Equal("https://public@example.invalid/2", info.Dsn);
            Assert.Equal("juvin@" + AppVersion.Current, info.Release);
#if DEBUG
            Assert.Equal("development", info.Environment);
#else
            Assert.Equal("production", info.Environment);
#endif
        }
        finally
        {
            Environment.SetEnvironmentVariable("JUVIN_SENTRY_DSN", previous);
        }
    }
}
