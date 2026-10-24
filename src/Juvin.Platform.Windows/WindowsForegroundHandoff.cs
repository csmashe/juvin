using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Juvin.Platform.Windows;

/// <summary>
///     Windows lets a process take the foreground only when the process in front allows it, so without this the
///     browser opens the sample tab behind the wizard. Any process may then come forward once, until the next input.
/// </summary>
public sealed partial class WindowsForegroundHandoff : IForegroundHandoff
{
    private const int AnyProcess = -1;

    public void AllowOtherAppToComeForward()
    {
        if (!AllowSetForegroundWindow(AnyProcess))
        {
            Trace.WriteLine($"[ForegroundHandoff] AllowSetForegroundWindow failed ({Marshal.GetLastPInvokeError()}).");
        }
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int processId);
}
