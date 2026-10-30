using Sentry.Protocol;
using SentryApp = Sentry.Protocol.App;
using SentryOperatingSystem = Sentry.Protocol.OperatingSystem;

namespace Juvin.App.Services.CrashReporting;

/// <summary>
///     Reduces a report to the app version, OS, runtime, exception types, and stack frames. Every free-text
///     field is dropped rather than pattern-scrubbed, so no email content, local path, or name can survive.
/// </summary>
internal static class CrashReportScrubber
{
    public const string Redacted = "<redacted>";
    public const string OperationTag = "operation";

    private const string DotNetContext = ".NET";
    private const string DynamicCodeContext = "Dynamic Code";
    private const string JuvinSourceRoot = "/_/src/Juvin.";

    private static readonly string[] s_dynamicCodeFlags = ["Compiled", "Supported"];

    private static readonly HashSet<string> s_allowedMechanisms = new(StringComparer.Ordinal)
    {
        CrashOperations.UiDispatcher,
        CrashOperations.UnobservedTask,
        CrashOperations.AppDomain,
    };

    private static readonly HashSet<string> s_allowedTags = new(StringComparer.Ordinal)
    {
        OperationTag,
        CrashReportErrorFacts.HResultTag,
        CrashReportErrorFacts.InnerTypeTag,
        CrashReportErrorFacts.HttpStatusTag,
        CrashReportErrorFacts.SocketErrorTag,
    };

    public static SentryEvent? Scrub(SentryEvent? e)
    {
        if (e is null)
        {
            return null;
        }

        e.ServerName = null;
        e.Message = null;
        e.Logger = null;
        e.TransactionName = null;
        e.Fingerprint = [];
        e.Distribution = null;
        e.User = new SentryUser();
        e.Request = new SentryRequest();
        ((IDictionary<string, object?>)e.Extra).Clear();
        RebuildContexts(e.Contexts);
        CrashReportErrorFacts.Apply(e);
        foreach (var key in e.Tags.Keys.Where(key => !s_allowedTags.Contains(key)).ToArray())
        {
            e.UnsetTag(key);
        }

        foreach (var exception in e.SentryExceptions ?? [])
        {
            exception.Value = Redacted;
            // The SDK fills the mechanism from Exception.Data and HelpLink; only the handled flags and our own type stay.
            if (exception.Mechanism is { } mechanism)
            {
                mechanism.Data.Clear();
                mechanism.Meta.Clear();
                mechanism.HelpLink = null;
                mechanism.Description = null;
                if (!s_allowedMechanisms.Contains(mechanism.Type))
                {
                    mechanism.Type = "generic";
                }
            }

            ScrubStack(exception.Stacktrace);
        }

        foreach (var thread in e.SentryThreads ?? [])
        {
            ScrubStack(thread.Stacktrace);
        }

        foreach (var image in e.DebugImages ?? [])
        {
            image.CodeFile = null;
            image.DebugFile = null;
        }

        return e;
    }

    // Exception.Data can replace any context with an arbitrary object, so each kept context is rebuilt from approved fields.
    private static void RebuildContexts(SentryContexts contexts)
    {
        var app = Find<SentryApp>(contexts, SentryApp.Type);
        var os = Find<SentryOperatingSystem>(contexts, SentryOperatingSystem.Type);
        var runtime = Find<Runtime>(contexts, Runtime.Type);
        var dotNet = Find<Runtime>(contexts, DotNetContext);
        var dynamicCode = Find<IDictionary<string, bool>>(contexts, DynamicCodeContext);
        contexts.Clear();
        if (app is not null)
        {
            contexts[SentryApp.Type] = new SentryApp { StartTime = app.StartTime, BuildType = app.BuildType };
        }

        if (os is not null)
        {
            contexts[SentryOperatingSystem.Type] = new SentryOperatingSystem
            {
                Name = os.Name,
                Version = os.Version,
                Build = os.Build,
                KernelVersion = os.KernelVersion,
                RawDescription = os.RawDescription,
            };
        }

        if (runtime is not null)
        {
            contexts[Runtime.Type] = CopyRuntime(runtime);
        }

        if (dotNet is not null)
        {
            contexts[DotNetContext] = CopyRuntime(dotNet);
        }

        if (dynamicCode is not null)
        {
            contexts[DynamicCodeContext] = s_dynamicCodeFlags
                .Where(dynamicCode.ContainsKey)
                .ToDictionary(flag => flag, flag => dynamicCode[flag]);
        }
    }

    private static T? Find<T>(SentryContexts contexts, string key)
        where T : class
    {
        return contexts.TryGetValue(key, out var value) ? value as T : null;
    }

    private static Runtime CopyRuntime(Runtime runtime)
    {
        return new Runtime
        {
            Name = runtime.Name,
            Version = runtime.Version,
            Build = runtime.Build,
            RawDescription = runtime.RawDescription,
        };
    }

    private static void ScrubStack(SentryStackTrace? stack)
    {
        foreach (var frame in stack?.Frames ?? [])
        {
            frame.FileName = JuvinSourcePathOrNull(frame.FileName);
            frame.AbsolutePath = JuvinSourcePathOrNull(frame.AbsolutePath);
        }
    }

    // Release builds map the repository root to /_/, so such a path names nothing on the user's machine.
    private static string? JuvinSourcePathOrNull(string? path)
    {
        return path?.StartsWith(JuvinSourceRoot, StringComparison.Ordinal) == true ? path : null;
    }
}
