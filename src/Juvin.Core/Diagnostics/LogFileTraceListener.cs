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
        try
        {
            _writer?.Flush();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _writer = null;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _writer?.Dispose();
            _writer = null;
        }

        base.Dispose(disposing);
    }

    private void Append(string? message, bool endLine)
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
        if (File.Exists(_path))
        {
            try
            {
                File.Move(_path, _previousPath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The current file is truncated below instead, so the log still stays bounded.
            }
        }

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
