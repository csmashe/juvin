// Throwaway Phase 0 spike: is a global hotkey (Ctrl+Shift+Y) delivered to a
// .NET process on each OS, and by which backend?
//
//   juvin-hotkey-spike selftest --backend sharphook|evdev|socket [--no-inject] [--timeout 8]
//   juvin-hotkey-spike trigger            (connects to the socket and sends "press")
//   juvin-hotkey-spike inject             (synthesises Ctrl+Shift+Y and exits)
//
// selftest starts the backend, waits briefly, injects the chord (unless
// --no-inject), and reports whether the press arrived and how long after
// injection. The socket backend expects a desktop-environment shortcut that
// runs "juvin-hotkey-spike trigger"; selftest installs that GNOME shortcut
// itself when --gnome is given and removes it afterwards.

using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using SharpHook;
using SharpHook.Native;

var args0 = args.Length > 0 ? args[0] : "help";
string Opt(string name, string fallback)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}
bool Flag(string name) => Array.IndexOf(args, name) >= 0;

switch (args0)
{
    case "trigger":
        return Trigger.Send();
    case "inject":
        Injector.InjectChord();
        return 0;
    case "selftest":
        return await SelfTest.RunAsync(Opt("--backend", "sharphook"), !Flag("--no-inject"),
            Flag("--gnome"), int.Parse(Opt("--timeout", "8")));
    default:
        Console.Error.WriteLine("usage: selftest --backend sharphook|evdev|socket [--no-inject] [--gnome] | trigger | inject");
        return 2;
}

static class SocketPath
{
    public static string Get()
    {
        if (OperatingSystem.IsWindows())
        {
            return @"\\.\pipe\juvin-hotkey-spike";
        }
        var dir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrEmpty(dir))
        {
            dir = Path.GetTempPath();
        }
        return Path.Join(dir, "juvin-hotkey-spike.sock");
    }
}

static class Trigger
{
    public static int Send()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", "juvin-hotkey-spike", System.IO.Pipes.PipeDirection.Out);
                pipe.Connect(2000);
                var bytes = Encoding.UTF8.GetBytes("press\n");
                pipe.Write(bytes);
                return 0;
            }
            using var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            s.Connect(new UnixDomainSocketEndPoint(SocketPath.Get()));
            s.Send(Encoding.UTF8.GetBytes("press\n"));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"trigger failed: {ex.Message}");
            return 1;
        }
    }
}

static class SelfTest
{
    public static async Task<int> RunAsync(string backend, bool inject, bool gnome, int timeoutSeconds)
    {
        var delivered = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnPress()
        {
            delivered.TrySetResult(Stopwatch.GetTimestamp());
        }

        using var cts = new CancellationTokenSource();
        IAsyncDisposable? listener;
        string? note = null;
        try
        {
            listener = backend switch
            {
                "sharphook" => SharpHookBackend.Start(OnPress),
                "evdev" => EvdevBackend.Start(OnPress, out note),
                "socket" => await SocketBackend.StartAsync(OnPress, cts.Token),
                _ => throw new ArgumentException($"unknown backend {backend}")
            };
        }
        catch (Exception ex)
        {
            Report(backend, false, null, $"start failed: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        GnomeShortcut? shortcut = null;
        if (gnome)
        {
            shortcut = GnomeShortcut.Install();
            note = (note is null ? "" : note + "; ") + shortcut.Note;
        }

        await Task.Delay(1500);
        long injectedAt = 0;
        if (inject)
        {
            injectedAt = Stopwatch.GetTimestamp();
            Injector.InjectChord();
        }
        else
        {
            Console.Error.WriteLine($"press Ctrl+Shift+Y within {timeoutSeconds} s");
        }

        var finished = await Task.WhenAny(delivered.Task, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds)));
        double? latencyMs = null;
        var ok = finished == delivered.Task;
        if (ok && inject)
        {
            latencyMs = Stopwatch.GetElapsedTime(injectedAt, delivered.Task.Result).TotalMilliseconds;
        }

        cts.Cancel();
        shortcut?.Remove();
        try
        {
            await listener.DisposeAsync();
        }
        catch
        {
        }
        Report(backend, ok, latencyMs, note);
        return ok ? 0 : 1;
    }

    private static void Report(string backend, bool delivered, double? latencyMs, string? note)
    {
        var json = JsonSerializer.Serialize(new
        {
            backend,
            os = RuntimeInformation.OSDescription,
            session = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"),
            delivered,
            latencyMs = latencyMs is null ? (double?)null : Math.Round(latencyMs.Value, 1),
            note
        });
        Console.WriteLine(json);
    }
}

sealed class SharpHookBackend : IAsyncDisposable
{
    private readonly TaskPoolGlobalHook _hook;
    private SharpHookBackend(TaskPoolGlobalHook hook)
    {
        _hook = hook;
    }

    public static SharpHookBackend Start(Action onPress)
    {
        var hook = new TaskPoolGlobalHook();
        hook.KeyPressed += (_, e) =>
        {
            var mask = e.RawEvent.Mask;
            var ctrl = (mask & (ModifierMask.LeftCtrl | ModifierMask.RightCtrl)) != 0;
            var shift = (mask & (ModifierMask.LeftShift | ModifierMask.RightShift)) != 0;
            if (ctrl && shift && e.Data.KeyCode == KeyCode.VcY)
            {
                onPress();
            }
        };
        hook.RunAsync();
        return new SharpHookBackend(hook);
    }

    public ValueTask DisposeAsync()
    {
        _hook.Dispose();
        return ValueTask.CompletedTask;
    }
}

// Minimal evdev reader: open every /dev/input/event* node that looks like a
// keyboard, read raw input_event records, track Ctrl/Shift state per device.
sealed class EvdevBackend : IAsyncDisposable
{
    private const int EvKey = 1;
    private const int KeyLeftCtrl = 29, KeyRightCtrl = 97, KeyLeftShift = 42, KeyRightShift = 54, KeyY = 21;
    private readonly List<FileStream> _streams = [];
    private readonly CancellationTokenSource _cts = new();

    public static EvdevBackend Start(Action onPress, out string note)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("evdev is Linux only");
        }
        var backend = new EvdevBackend();
        var opened = 0;
        var denied = 0;
        var keyboards = 0;
        foreach (var path in Directory.EnumerateFiles("/dev/input", "event*").OrderBy(p => p))
        {
            FileStream fs;
            try
            {
                fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.None);
            }
            catch (UnauthorizedAccessException)
            {
                denied++;
                continue;
            }
            catch (IOException)
            {
                continue;
            }
            if (!IsKeyboard(fs.SafeFileHandle.DangerousGetHandle()))
            {
                fs.Dispose();
                continue;
            }
            keyboards++;
            opened++;
            backend._streams.Add(fs);
            var pressed = new HashSet<int>();
            _ = Task.Factory.StartNew(() => backend.ReadLoop(fs, pressed, onPress), TaskCreationOptions.LongRunning);
        }
        note = $"evdev: {opened} keyboard nodes opened, {denied} denied (groups: {string.Join(",", GetGroups())})";
        if (keyboards == 0)
        {
            throw new InvalidOperationException(note);
        }
        return backend;
    }

    private static IEnumerable<string> GetGroups()
    {
        try
        {
            var psi = new ProcessStartInfo("id", "-Gn") { RedirectStandardOutput = true };
            using var p = Process.Start(psi)!;
            var s = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            return s.Split(' ');
        }
        catch
        {
            return [];
        }
    }

    private void ReadLoop(FileStream fs, HashSet<int> pressed, Action onPress)
    {
        var size = IntPtr.Size == 8 ? 24 : 16;
        var buf = new byte[size];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var read = 0;
                while (read < size)
                {
                    var n = fs.Read(buf, read, size - read);
                    if (n <= 0)
                    {
                        return;
                    }
                    read += n;
                }
                var type = BitConverter.ToUInt16(buf, size - 8);
                var code = BitConverter.ToUInt16(buf, size - 6);
                var value = BitConverter.ToInt32(buf, size - 4);
                if (type != EvKey)
                {
                    continue;
                }
                if (value == 0)
                {
                    pressed.Remove(code);
                    continue;
                }
                if (value == 1)
                {
                    pressed.Add(code);
                }
                var ctrl = pressed.Contains(KeyLeftCtrl) || pressed.Contains(KeyRightCtrl);
                var shift = pressed.Contains(KeyLeftShift) || pressed.Contains(KeyRightShift);
                if (value == 1 && code == KeyY && ctrl && shift)
                {
                    onPress();
                }
            }
        }
        catch
        {
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(IntPtr fd, nuint request, byte[] buf);

    private static bool IsKeyboard(IntPtr fd)
    {
        // EVIOCGBIT(EV_KEY, 96) = _IOC(_IOC_READ, 'E', 0x20 + 1, 96)
        const int bytes = 96;
        nuint request = (2u << 30) | ((uint)bytes << 16) | (0x45u << 8) | (0x20u + EvKey);
        var bits = new byte[bytes];
        if (ioctl(fd, request, bits) < 0)
        {
            return false;
        }
        return Bit(bits, 28) && Bit(bits, 30) && Bit(bits, 44) && Bit(bits, 57);
    }

    private static bool Bit(byte[] bits, int i) => (bits[i / 8] & (1 << (i % 8))) != 0;

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        foreach (var s in _streams)
        {
            try
            {
                s.Dispose();
            }
            catch
            {
            }
        }
        return ValueTask.CompletedTask;
    }
}

sealed class SocketBackend : IAsyncDisposable
{
    private Socket? _socket;
    private System.IO.Pipes.NamedPipeServerStream? _pipe;

    public static async Task<SocketBackend> StartAsync(Action onPress, CancellationToken ct)
    {
        var backend = new SocketBackend();
        if (OperatingSystem.IsWindows())
        {
            var pipe = new System.IO.Pipes.NamedPipeServerStream("juvin-hotkey-spike", System.IO.Pipes.PipeDirection.In, 1, System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
            backend._pipe = pipe;
            _ = Task.Run(async () =>
            {
                try
                {
                    await pipe.WaitForConnectionAsync(ct);
                    var buf = new byte[16];
                    var n = await pipe.ReadAsync(buf, ct);
                    if (n > 0)
                    {
                        onPress();
                    }
                }
                catch
                {
                }
            }, ct);
            return backend;
        }

        var path = SocketPath.Get();
        File.Delete(path);
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Bind(new UnixDomainSocketEndPoint(path));
        socket.Listen(4);
        backend._socket = socket;
        _ = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    using var client = await socket.AcceptAsync(ct);
                    var buf = new byte[16];
                    var n = await client.ReceiveAsync(buf, ct);
                    if (n > 0)
                    {
                        onPress();
                    }
                }
            }
            catch
            {
            }
        }, ct);
        await Task.CompletedTask;
        return backend;
    }

    public ValueTask DisposeAsync()
    {
        _socket?.Dispose();
        _pipe?.Dispose();
        if (!OperatingSystem.IsWindows())
        {
            File.Delete(SocketPath.Get());
        }
        return ValueTask.CompletedTask;
    }
}

// GNOME custom shortcut under org.gnome.settings-daemon.plugins.media-keys,
// appended to the user's list and removed again afterwards.
sealed class GnomeShortcut
{
    private const string Schema = "org.gnome.settings-daemon.plugins.media-keys";
    private const string Path0 = "/org/gnome/settings-daemon/plugins/media-keys/custom-keybindings/juvin-spike/";
    private readonly string _original;
    public string Note { get; }

    private GnomeShortcut(string original, string note)
    {
        _original = original;
        Note = note;
    }

    public static GnomeShortcut Install()
    {
        var original = Gsettings("get", Schema, "custom-keybindings").Trim();
        var list = original is "@as []" or "[]" ? new List<string>() : original.Trim('[', ']').Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim().Trim('\'')).ToList();
        if (!list.Contains(Path0))
        {
            list.Add(Path0);
        }
        var merged = "[" + string.Join(", ", list.Select(p => $"'{p}'")) + "]";
        Gsettings("set", Schema, "custom-keybindings", merged);
        var exe = Environment.ProcessPath!;
        var s = $"{Schema}.custom-keybinding:{Path0}";
        Gsettings("set", s, "name", "Juvin spike");
        Gsettings("set", s, "command", $"{exe} trigger");
        Gsettings("set", s, "binding", "<Control><Shift>y");
        return new GnomeShortcut(original, "gnome custom shortcut <Control><Shift>y installed via gsettings");
    }

    public void Remove()
    {
        Gsettings("reset-recursively", $"{Schema}.custom-keybinding:{Path0}");
        Gsettings("set", Schema, "custom-keybindings", _original);
    }

    private static string Gsettings(params string[] a)
    {
        var psi = new ProcessStartInfo("gsettings") { RedirectStandardOutput = true };
        foreach (var x in a)
        {
            psi.ArgumentList.Add(x);
        }
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return o;
    }
}

static class Injector
{
    public static void InjectChord()
    {
        if (OperatingSystem.IsWindows())
        {
            Win32SendInput();
            return;
        }
        // ydotool key codes: KEY_LEFTCTRL 29, KEY_LEFTSHIFT 42, KEY_Y 21
        // ydotool 1.x takes code:state pairs; 0.1.x takes names such as ctrl+shift+y.
        var keys = Environment.GetEnvironmentVariable("JUVIN_YDOTOOL_KEYS") ?? "29:1 42:1 21:1 21:0 42:0 29:0";
        var psi = new ProcessStartInfo("ydotool") { RedirectStandardError = true };
        foreach (var x in new[] { "key" }.Concat(keys.Split(' ', StringSplitOptions.RemoveEmptyEntries)))
        {
            psi.ArgumentList.Add(x);
        }
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            Console.Error.WriteLine($"ydotool failed ({p.ExitCode}): {err.Trim()}");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public KEYBDINPUT ki;
        [FieldOffset(8)] public MOUSEINPUT mi;
    }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint n, INPUT[] inputs, int size);

    private static void Win32SendInput()
    {
        const uint keyUp = 2;
        ushort[] keys = [0x11, 0x10, 0x59];
        var list = new List<INPUT>();
        foreach (var k in keys)
        {
            list.Add(new INPUT { type = 1, ki = new KEYBDINPUT { wVk = k } });
        }
        foreach (var k in keys.Reverse())
        {
            list.Add(new INPUT { type = 1, ki = new KEYBDINPUT { wVk = k, dwFlags = keyUp } });
        }
        var sent = SendInput((uint)list.Count, list.ToArray(), Marshal.SizeOf<INPUT>());
        if (sent != list.Count)
        {
            Console.Error.WriteLine($"SendInput sent {sent}/{list.Count}, error {Marshal.GetLastPInvokeError()}, cbSize {Marshal.SizeOf<INPUT>()}");
        }
    }
}
