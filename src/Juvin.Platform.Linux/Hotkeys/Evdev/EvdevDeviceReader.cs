using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Juvin.Platform.Linux.Hotkeys.Evdev;

/// <summary>
///     Reads <see cref="InputEvent" /> records from one <c>/dev/input/eventN</c> node without a grab, so
///     other readers still get the keys. Only the polling loop closes the handle, so a descriptor is
///     never closed under a pending read.
/// </summary>
internal sealed partial class EvdevDeviceReader : IEvdevDeviceReader
{
    private const int PollTimeoutMilliseconds = 250;
    private const short PollIn = 0x001;
    private const short PollError = 0x008;
    private const short PollHangUp = 0x010;
    private const short PollInvalid = 0x020;
    private const int ErrorInterrupted = 4;

    // Never disposed: it has no timer or registrations, and disposing would race Dispose's Cancel.
    private readonly CancellationTokenSource _cts = new();
    private readonly SafeFileHandle _handle;
    private readonly Action<string, InputEvent> _onEvent;
    private readonly Action<string> _onFailed;

    private EvdevDeviceReader(string path, SafeFileHandle handle, Action<string, InputEvent> onEvent, Action<string> onFailed)
    {
        Path = path;
        _handle = handle;
        _onEvent = onEvent;
        _onFailed = onFailed;
    }

    public string Path { get; }

    public static EvdevDeviceReader? TryOpen(string path, Action<string, InputEvent> onEvent, Action<string> onFailed)
    {
        SafeFileHandle handle;
        try
        {
            handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"[EvdevDeviceReader] Opening {path} failed ({ex.GetType().Name}).");
            return null;
        }

        var reader = new EvdevDeviceReader(path, handle, onEvent, onFailed);
        Task.Factory.StartNew(reader.Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        return reader;
    }

    public void Dispose()
    {
        _cts.Cancel();
    }

    private void Run()
    {
        var ct = _cts.Token;
        var failed = false;
        var buffer = new byte[InputEvent.SizeBytes];
        try
        {
            using var stream = new FileStream(_handle, FileAccess.Read, bufferSize: 0);
            while (!ct.IsCancellationRequested)
            {
                var descriptor = new PollDescriptor { FileDescriptor = (int)_handle.DangerousGetHandle(), Events = PollIn };
                var ready = poll(ref descriptor, 1, PollTimeoutMilliseconds);
                if (ready < 0 && Marshal.GetLastPInvokeError() == ErrorInterrupted)
                {
                    continue;
                }

                if (ready < 0 || (descriptor.ReturnedEvents & (PollError | PollHangUp | PollInvalid)) != 0)
                {
                    failed = true;
                    break;
                }

                if (ready == 0 || ct.IsCancellationRequested)
                {
                    continue;
                }

                // evdev returns whole events, so one read of one event's size yields exactly one.
                if (stream.Read(buffer) != buffer.Length)
                {
                    failed = true;
                    break;
                }

                _onEvent(Path, MemoryMarshal.Read<InputEvent>(buffer));
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[EvdevDeviceReader] Reading {Path} stopped ({ex.GetType().Name}).");
            failed = true;
        }
        finally
        {
            _handle.Dispose();
        }

        if (failed && !ct.IsCancellationRequested)
        {
            _onFailed(Path);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollDescriptor
    {
        public int FileDescriptor;
        public short Events;
        public short ReturnedEvents;
    }

    // ReSharper disable once InconsistentNaming -- the libc function name
    [LibraryImport("libc", SetLastError = true)]
    private static partial int poll(ref PollDescriptor descriptors, nuint count, int timeout);
}
