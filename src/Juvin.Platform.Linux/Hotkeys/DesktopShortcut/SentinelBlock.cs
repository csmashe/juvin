using Juvin.Core.Services;

namespace Juvin.Platform.Linux.Hotkeys.DesktopShortcut;

/// <summary>
///     The block of lines Juvin owns in a compositor config, between two marker comments, so it can
///     be replaced or removed without touching the rest. A file with stray or doubled markers is
///     refused rather than guessed at.
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
        return open < 0 ? null : lines[(open + 1)..close].Select(line => line.TrimEnd()).ToList();
    }

    public static string Upsert(string contents, IEnumerable<string> managed)
    {
        var lines = SplitLines(contents);
        var (open, close) = Find(lines);
        List<string> block = [Open, .. managed, Close];
        if (open >= 0)
        {
            lines.RemoveRange(open, close - open + 1);
            lines.InsertRange(open, block);
        }
        else
        {
            if (lines.Count > 0 && lines[^1].Length > 0)
            {
                lines.Add("");
            }

            lines.AddRange(block);
        }

        return string.Join('\n', lines) + "\n";
    }

    public static string Remove(string contents)
    {
        var lines = SplitLines(contents);
        var (open, close) = Find(lines);
        if (open < 0)
        {
            return contents;
        }

        lines.RemoveRange(open, close - open + 1);
        if (open > 0 && open == lines.Count && lines[open - 1].Length == 0)
        {
            lines.RemoveAt(open - 1);
        }

        return lines.Count == 0 ? "" : string.Join('\n', lines) + "\n";
    }

    /// <summary>
    ///     Writes <paramref name="contents" /> atomically unless the file already holds exactly that.
    ///     A symlinked config, as dotfile managers make, is written through to its target.
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
        AtomicFile.WriteAllText(target, contents);
        return true;
    }

    public static string? ReadFile(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

    private static List<string> SplitLines(string contents)
    {
        var lines = contents.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }

    private static (int Open, int Close) Find(List<string> lines)
    {
        var opens = lines.Select((line, index) => (line, index)).Where(l => l.line.TrimEnd().StartsWith(OpenPrefix, StringComparison.Ordinal)).ToList();
        var closes = lines.Select((line, index) => (line, index)).Where(l => l.line.TrimEnd() == Close).ToList();
        return (opens.Count, closes.Count) switch
        {
            (0, 0) => (-1, -1),
            (1, 1) when opens[0].index < closes[0].index => (opens[0].index, closes[0].index),
            _ => throw new InvalidOperationException(
                $"The Juvin block markers in the file are unbalanced ({opens.Count} opening, {closes.Count} closing). Remove the stray marker lines and try again."
            ),
        };
    }
}
