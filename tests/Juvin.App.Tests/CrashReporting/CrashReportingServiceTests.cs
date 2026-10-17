using System.Reflection;
using System.Text.Json;
using Juvin.App.Services.CrashReporting;
using Juvin.App.Tests.Fakes;

namespace Juvin.App.Tests.CrashReporting;

[Collection(SentrySdkCollection.Name)]
public sealed class CrashReportingServiceTests
{
    private const string Canary = "draft: meet at noon about the merger";
    private static readonly TimeSpan s_flushTimeout = TimeSpan.FromSeconds(5);
    private readonly FakeSettingsService _settings = new();

    public CrashReportingServiceTests()
    {
        _settings.Update(s => s with { CrashReportingEnabled = false, HasCompletedOnboarding = true });
    }

    [Fact]
    public void WhileTheSettingIsOff_NothingIsCaptured()
    {
        using var handler = new RecordingHttpHandler();
        using var service = Create(handler);

        service.Start();
        service.CaptureException(new InvalidOperationException(Canary), "test");
        service.Flush(s_flushTimeout);

        Assert.False(service.IsEnabled);
        Assert.Null(service.ConsentGate);
        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public void TurningTheSettingOnAndOff_StartsAndStopsTheSdkBehindAFreshGate()
    {
        using var handler = new RecordingHttpHandler();
        using var service = Create(handler);
        try
        {
            service.Start();
            _settings.Update(s => s with { CrashReportingEnabled = true });
            Assert.True(service.IsEnabled);
            var first = service.ConsentGate!;
            Assert.True(first.IsOpen);

            _settings.Update(s => s with { CrashReportingEnabled = false });
            Assert.False(service.IsEnabled);
            Assert.False(first.IsOpen);

            _settings.Update(s => s with { CrashReportingEnabled = true });
            Assert.NotSame(first, service.ConsentGate);
            Assert.True(service.ConsentGate!.IsOpen);
        }
        finally
        {
            SentrySdk.Close();
        }
    }

    [Fact]
    public void AStaleEnabledNotification_AfterOptOut_DoesNotRestartTheSdk()
    {
        _settings.Update(s => s with { CrashReportingEnabled = true });
        using var handler = new RecordingHttpHandler();
        using var service = Create(handler);
        try
        {
            service.Start();
            var stale = _settings.Current;
            _settings.Update(s => s with { CrashReportingEnabled = false });

            _settings.RaiseSettingsChanged(stale);

            Assert.False(service.IsEnabled);
        }
        finally
        {
            SentrySdk.Close();
        }
    }

    [Fact]
    public void EnablingAgainAfterOptOut_StillSendsThroughTheInjectedHandler()
    {
        using var handler = new RecordingHttpHandler();
        using var service = Create(handler);
        try
        {
            service.Start();
            _settings.Update(s => s with { CrashReportingEnabled = true });
            _settings.Update(s => s with { CrashReportingEnabled = false });
            _settings.Update(s => s with { CrashReportingEnabled = true });

            service.CaptureException(new InvalidOperationException(Canary), "test");
            service.Flush(s_flushTimeout);

            Assert.False(handler.IsDisposed);
            Assert.Single(handler.Bodies);
        }
        finally
        {
            SentrySdk.Close();
        }
    }

    [Fact]
    public void AnUnobservedCancelledTask_SendsNothing()
    {
        _settings.Update(s => s with { CrashReportingEnabled = true });
        using var handler = new RecordingHttpHandler();
        using var service = Create(handler);
        try
        {
            service.Start();
            var cancelled = new AggregateException(new TaskCanceledException(), new AggregateException(new OperationCanceledException()));

            service.CaptureException(cancelled, CrashOperations.UnobservedTask);
            service.Flush(s_flushTimeout);

            Assert.Empty(handler.Bodies);
        }
        finally
        {
            SentrySdk.Close();
        }
    }

    [Fact]
    public void AReservedContextFortyLevelsDown_NeverReachesTheEnvelope()
    {
        _settings.Update(s => s with { CrashReportingEnabled = true });
        using var handler = new RecordingHttpHandler();
        using var service = Create(handler);
        try
        {
            service.Start();
            Exception exception = new IOException { Data = { ["sentry:context:runtime"] = new Dictionary<string, string> { ["draft"] = Canary } } };
            for (var depth = 0; depth < 40; depth++)
            {
                exception = new InvalidOperationException(null, exception);
            }

            service.CaptureException(exception, "test");
            service.Flush(s_flushTimeout);

            var body = Assert.Single(handler.Bodies);
            Assert.DoesNotContain("merger", body);
        }
        finally
        {
            SentrySdk.Close();
        }
    }

    [Fact]
    public async Task ACyclicExceptionGraph_IsStrippedWithoutHanging()
    {
        var first = new InvalidOperationException { Data = { ["sentry:tag:thread"] = Canary } };
        var second = new AggregateException(new IOException(null, first));
        typeof(Exception).GetField("_innerException", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(first, second);

        await Task.Run(() => CrashReportingService.StripReservedData(second)).WaitAsync(s_flushTimeout);

        Assert.Empty(first.Data);
    }

    [Fact]
    public void BeforeOnboardingIsComplete_TheSettingStartsNothingUntilItCompletes()
    {
        _settings.Update(s => s with { CrashReportingEnabled = true, HasCompletedOnboarding = false });
        using var handler = new RecordingHttpHandler();
        using var service = Create(handler);
        try
        {
            service.Start();
            Assert.False(service.IsEnabled);
            Assert.Null(service.ConsentGate);

            _settings.Update(s => s with { HasCompletedOnboarding = true });
            Assert.True(service.IsEnabled);
            Assert.True(service.ConsentGate!.IsOpen);
        }
        finally
        {
            SentrySdk.Close();
        }
    }

    [Fact]
    public void WithoutAnEndpoint_TheSettingStartsNothing()
    {
        _settings.Update(s => s with { CrashReportingEnabled = true });
        using var service = new CrashReportingService(_settings, new CrashReportBuildInfo(null, "juvin@test", "test"), null, null);

        service.Start();

        Assert.False(service.IsAvailable);
        Assert.False(service.IsEnabled);
    }

    [Fact]
    public void ACapturedException_SendsOneEnvelopeWithoutContent()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var user = Environment.UserName;
        _settings.Update(s => s with { CrashReportingEnabled = true });
        using var handler = new RecordingHttpHandler();
        using var service = Create(handler);
        try
        {
            service.Start();
            var exception = Thrown(home);
            exception.Data["thread"] = Canary;
            exception.HelpLink = "https://mail.example/draft?subject=" + Uri.EscapeDataString(Canary);
            exception.Data["sentry:context:runtime"] = new Dictionary<string, string> { ["draft"] = Canary };
            exception.Data["sentry:tag:operation"] = Canary;
            exception.Data["Sentry:Tag:thread"] = Canary;

            service.CaptureException(exception, CrashOperations.UiDispatcher, fatal: true);
            service.Flush(s_flushTimeout);

            var body = Assert.Single(handler.Bodies);
            Assert.DoesNotContain(Canary, body);
            Assert.DoesNotContain("merger", body);
            Assert.DoesNotContain(home, body);
            if (user.Length >= 3)
            {
                Assert.DoesNotContain(user, body);
            }

            Assert.Contains("juvin@test", body);
            Assert.Contains("\"operation\":\"ui-dispatcher\"", body);
            Assert.DoesNotContain("mail.example", body);
            var exceptionValues = body
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonDocument.Parse(line).RootElement)
                .Where(root => root.TryGetProperty("exception", out _))
                .Select(root => root.GetProperty("exception").GetProperty("values").EnumerateArray().ToArray())
                .Single();
            Assert.Equal(
                ["System.IO.IOException", "System.InvalidOperationException"],
                exceptionValues.Select(value => value.GetProperty("type").GetString()!).ToArray()
            );
            Assert.All(exceptionValues, value => Assert.Equal(CrashReportScrubber.Redacted, value.GetProperty("value").GetString()));
            var outer = exceptionValues[^1];
            Assert.NotEmpty(outer.GetProperty("stacktrace").GetProperty("frames").EnumerateArray());
            var mechanism = outer.GetProperty("mechanism");
            Assert.Equal(CrashOperations.UiDispatcher, mechanism.GetProperty("type").GetString());
            Assert.False(mechanism.GetProperty("handled").GetBoolean());
        }
        finally
        {
            SentrySdk.Close();
        }
    }

    private CrashReportingService Create(HttpMessageHandler handler)
    {
        return new CrashReportingService(
            _settings,
            new CrashReportBuildInfo("https://public@example.invalid/1", "juvin@test", "test"),
            handler,
            null
        );
    }

    private static InvalidOperationException Thrown(string home)
    {
        try
        {
            var inner = new IOException(Canary) { Data = { ["sentry:tag:inner"] = Canary } };
            throw new InvalidOperationException($"{Canary} in {home}", inner);
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }
    }
}
