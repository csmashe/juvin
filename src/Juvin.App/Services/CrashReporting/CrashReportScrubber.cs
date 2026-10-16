namespace Juvin.App.Services.CrashReporting;

/// <summary>
///     Reduces a report to the app version, OS, runtime, exception types, and stack frames. Every free-text
///     field is dropped rather than pattern-scrubbed, so no email content, path, or name can survive.
/// </summary>
internal static class CrashReportScrubber
{
    public const string Redacted = "<redacted>";
    public const string OperationTag = "operation";

    private static readonly HashSet<string> s_allowedContexts = new(StringComparer.OrdinalIgnoreCase)
    {
        "app",
        "os",
        "runtime",
        ".NET",
        "Dynamic Code",
    };

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
        e.User = new SentryUser();
        e.Request = new SentryRequest();
        ((IDictionary<string, object?>)e.Extra).Clear();
        foreach (var key in e.Contexts.Keys.Where(key => !s_allowedContexts.Contains(key)).ToArray())
        {
            e.Contexts.Remove(key);
        }

        foreach (var app in e.Contexts.Values.OfType<Sentry.Protocol.App>())
        {
            app.Hash = null;
            app.Identifier = null;
        }

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

    private static void ScrubStack(SentryStackTrace? stack)
    {
        foreach (var frame in stack?.Frames ?? [])
        {
            frame.FileName = null;
            frame.AbsolutePath = null;
        }
    }
}
