using System.Diagnostics;
using System.Globalization;
using System.Text;
using Juvin.Core.Services;

namespace Juvin.Core.Diagnostics;

/// <summary>
///     Writes trace output to <c>juvin.log</c>, each line timestamped. The previous file moves to
///     <c>juvin.previous.log</c> when the listener opens and when the current one reaches the size
///     limit, so the log stays bounded. A failed write is dropped; logging never stops the app.
/// </summary>
public sealed class LogFileTraceListener : TraceListener
{
    public const string FileName = "juvin.log";
    public const string PreviousFileName = "juvin.previous.log";
    private const long DefaultMaxBytes = 1024 * 1024;

    private static readonly UTF8Encoding s_utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    // Trace serializes writes, but Clear comes from the UI thread.
    private readonly Lock _gate = new();
    private readonly string _path;
    private readonly string _previousPath;
    private readonly long _maxBytes;
    private StreamWriter? _writer;
    private bool _atLineStart = true;

    /// <summary>Opens a new log in <paramref name="directory" />; throws if the file cannot be created.</summary>
    public LogFileTraceListener(string directory, long maxBytes = DefaultMaxBytes)
    {
        _path = Path.Join(directory, FileName);
        _previousPath = Path.Join(directory, PreviousFileName);
        _maxBytes = maxBytes;
        _writer = OpenAfterRotating();
    }

    /// <summary>
    ///     Removes the logs in <paramref name="directory" />. A listener open on that directory starts its
    ///     file again empty, because the open file cannot be deleted on Windows.
    /// </summary>
    public static void ClearIn(string directory)
    {
        var path = Path.Join(directory, FileName);
        var open = Trace.Listeners.OfType<LogFileTraceListener>().FirstOrDefault(listener => listener._path == path);
        if (open is not null)
        {
            open.Clear();
            return;
        }

        if (!Directory.Exists(directory))
        {
            return;
        }

        File.Delete(path);
        File.Delete(Path.Join(directory, PreviousFileName));
    }

    public override void Write(string? message)
    {
        Append(message, endLine: false);
    }

    public override void WriteLine(string? message)
    {
        Append(message, endLine: true);
    }

    public override void Flush()
    {
        lock (_gate)
        {
            try
            {
                _writer?.Flush();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                _writer = null;
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_gate)
            {
                _writer?.Dispose();
                _writer = null;
            }
        }

        base.Dispose(disposing);
    }

    private void Clear()
    {
        lock (_gate)
        {
            // First, so a failed delete leaves the current log open to record the failure.
            File.Delete(_previousPath);
            _writer?.Dispose();
            _writer = null;
            _atLineStart = true;
            _writer = Open();
        }
    }

    private void Append(string? message, bool endLine)
    {
        lock (_gate)
        {
            AppendLocked(message, endLine);
        }
    }

    private void AppendLocked(string? message, bool endLine)
    {
        var writer = _writer;
        if (writer is null)
        {
            return;
        }

        try
        {
            if (_atLineStart)
            {
                writer.Write(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff ", CultureInfo.InvariantCulture));
            }

            writer.Write(message);
            if (endLine)
            {
                writer.WriteLine();
            }

            _atLineStart = endLine;
            if (!_atLineStart || writer.BaseStream.Length < _maxBytes)
            {
                return;
            }

            writer.Dispose();
            _writer = OpenAfterRotating();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            _writer = null;
        }
    }

    private StreamWriter OpenAfterRotating()
    {
        if (!File.Exists(_path))
        {
            return Open();
        }

        try
        {
            File.Move(_path, _previousPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Open truncates the current file instead, so the log still stays bounded.
        }

        return Open();
    }

    private StreamWriter Open()
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.Read | FileShare.Delete,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = AtomicFile.OwnerReadWrite;
        }

        return new StreamWriter(new FileStream(_path, options), s_utf8NoBom) { AutoFlush = true };
    }
}
