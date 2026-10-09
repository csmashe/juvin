using System.Diagnostics;
using System.Security.Principal;

namespace Juvin.Platform.Windows.Ipc;

internal static class PipeNames
{
    /// <summary>
    ///     Pipe names are machine-wide, so the user's SID and the session id scope the name the same
    ///     way the session-local single-instance mutex is scoped.
    /// </summary>
    public static string Control =>
        $"juvin-control-{WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName}-{Process.GetCurrentProcess().SessionId}";
}
