using Juvin.Core.Services;

namespace Juvin.Platform.Linux.Hotkeys.DesktopShortcut;

/// <summary>
///     The block of lines Juvin owns in a compositor config, between two marker comments, so it can
///     be replaced or removed without touching the rest. Edits keep the file's own line endings and
///     whether it ends in a newline. A file with stray or doubled markers is refused rather than
///     guessed at.
/// </summary>
internal static class SentinelBlock
{
    public const string Open = "# >>> juvin:rewrite (managed by Juvin; do not edit between these lines)";
    public const string Close = "# <<< juvin:rewrite";
    private const string OpenPrefix = "# >>> juvin:rewrite";

    /// <summary>The lines between the markers, or null when there is no block.</summary>
    public static IReadOnlyList<string>? Read(string contents)
    {
        var lines = SplitLines(contents);
        var (open, close) = Find(lines);
        return open < 0 ? null : lines[(open + 1)..close].Select(line => line.Text.TrimEnd()).ToList();
    }

    public static string Upsert(string contents, IEnumerable<string> managed)
    {
        var lines = SplitLines(contents);
        var (open, close) = Find(lines);
        var newline = NewlineOf(lines);
        string finalEnding;
        if (open >= 0)
        {
            finalEnding = lines[close].Ending;
            lines.RemoveRange(open, close - open + 1);
        }
        else
        {
            finalEnding = lines.Count == 0 ? newline : lines[^1].Ending;
            if (lines.Count > 0)
            {
                // Always a separator, even after a blank last line, so Remove can take exactly it back out.
                lines[^1] = lines[^1] with { Ending = newline };
                lines.Add(new Line("", newline));
            }

            open = lines.Count;
        }

        List<Line> block = [.. new[] { Open }.Concat(managed).Append(Close).Select(text => new Line(text, newline))];
        block[^1] = block[^1] with { Ending = finalEnding };
        lines.InsertRange(open, block);
        return Join(lines);
    }

    public static string Remove(string contents)
    {
        var lines = SplitLines(contents);
        var (open, close) = Find(lines);
        if (open < 0)
        {
            return contents;
        }

        var finalEnding = lines[close].Ending;
        lines.RemoveRange(open, close - open + 1);
        if (open != lines.Count)
        {
            return Join(lines);
        }

        // The block ended the file: drop the blank line Upsert put before it, and keep a missing final newline missing.
        if (open > 0 && lines[open - 1].Text.Length == 0)
        {
            lines.RemoveAt(open - 1);
        }

        if (lines.Count > 0 && finalEnding.Length == 0)
        {
            lines[^1] = lines[^1] with { Ending = "" };
        }

        return Join(lines);
    }

    /// <summary>Adds <paramref name="line" /> after the last line, in the file's line endings.</summary>
    public static string AppendLine(string contents, string line)
    {
        var lines = SplitLines(contents);
        var newline = NewlineOf(lines);
        if (lines.Count > 0)
        {
            lines[^1] = lines[^1] with { Ending = newline };
        }

        lines.Add(new Line(line, newline));
        return Join(lines);
    }

    /// <summary>
    ///     Writes <paramref name="contents" /> atomically unless the file already holds exactly that, keeping an
    ///     existing file's permissions. A symlinked config, as dotfile managers make, is written through to its target.
    /// </summary>
    public static bool WriteIfChanged(string path, string? existing, string contents)
    {
        if (existing == contents)
        {
            return false;
        }

        var info = new FileInfo(path);
        var target = info.LinkTarget is null ? path : info.ResolveLinkTarget(returnFinalTarget: true)!.FullName;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var mode = File.Exists(target) ? File.GetUnixFileMode(target) : AtomicFile.OwnerReadWrite;
        AtomicFile.WriteAllText(target, contents, mode);
        return true;
    }

    public static string? ReadFile(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

    private static List<Line> SplitLines(string contents)
    {
        var lines = new List<Line>();
        var start = 0;
        while (start < contents.Length)
        {
            var newline = contents.IndexOf('\n', start);
            if (newline < 0)
            {
                lines.Add(new Line(contents[start..], ""));
                break;
            }

            var end = newline > start && contents[newline - 1] == '\r' ? newline - 1 : newline;
            lines.Add(new Line(contents[start..end], contents[end..(newline + 1)]));
            start = newline + 1;
        }

        return lines;
    }

    /// <summary>The first line ending in the file, so added lines match it; a file without one gets LF.</summary>
    private static string NewlineOf(List<Line> lines) => lines.FirstOrDefault(line => line.Ending.Length > 0)?.Ending ?? "\n";

    private static string Join(List<Line> lines) => string.Concat(lines.Select(line => line.Text + line.Ending));

    private static (int Open, int Close) Find(List<Line> lines)
    {
        var opens = lines.Select((line, index) => (line, index)).Where(l => l.line.Text.TrimEnd().StartsWith(OpenPrefix, StringComparison.Ordinal)).ToList();
        var closes = lines.Select((line, index) => (line, index)).Where(l => l.line.Text.TrimEnd() == Close).ToList();
        return (opens.Count, closes.Count) switch
        {
            (0, 0) => (-1, -1),
            (1, 1) when opens[0].index < closes[0].index => (opens[0].index, closes[0].index),
            _ => throw new InvalidOperationException(
                $"The Juvin block markers in the file are unbalanced ({opens.Count} opening, {closes.Count} closing). Remove the stray marker lines and try again."
            ),
        };
    }

    /// <param name="Text">The line without its ending.</param>
    /// <param name="Ending"><c>\n</c>, <c>\r\n</c>, or empty for a last line without a newline.</param>
    private sealed record Line(string Text, string Ending);
}
