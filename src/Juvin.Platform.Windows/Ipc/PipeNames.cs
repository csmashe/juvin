using System.Diagnostics;
using System.Security.Principal;

namespace Juvin.Platform.Windows.Ipc;

/// <summary>
///     Pipe names are machine-wide, so the user's SID and the session id scope each name the same
///     way the session-local single-instance mutex is scoped.
/// </summary>
internal static class PipeNames
{
    public static string Control => Scoped("juvin-control");

    public static string Connector => Scoped("juvin-connector");

    private static string Scoped(string prefix) =>
        $"{prefix}-{WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName}-{Process.GetCurrentProcess().SessionId}";
}
