namespace Juvin.App.Services.CrashReporting;

public interface ICrashReporter
{
    /// <summary>False when this build has no endpoint, so the setting cannot turn reporting on.</summary>
    bool IsAvailable { get; }

    /// <summary>Sends a content-free report while crash reporting is on; does nothing while it is off.</summary>
    /// <param name="exception">The failure to report.</param>
    /// <param name="operation">Where it was caught; one of <see cref="CrashOperations" /> for the app's own hooks.</param>
    /// <param name="fatal">True when the process ends after this capture, so the report is marked unhandled.</param>
    // ReSharper disable once UnusedMemberInSuper.Global -- the app's hooks hold the service itself; this is the seam view models report through
    void CaptureException(Exception exception, string operation, bool fatal = false);
}

public static class CrashOperations
{
    public const string UiDispatcher = "ui-dispatcher";
    public const string UnobservedTask = "unobserved-task";
    public const string AppDomain = "appdomain";
}
