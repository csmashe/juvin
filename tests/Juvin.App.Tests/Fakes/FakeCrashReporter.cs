using Juvin.App.Services.CrashReporting;

namespace Juvin.App.Tests.Fakes;

public sealed class FakeCrashReporter : ICrashReporter
{
    public bool IsAvailable { get; set; } = true;

    public void CaptureException(Exception exception, string operation, bool fatal = false)
    {
    }
}
