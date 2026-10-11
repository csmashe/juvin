using System.Text;
using System.Text.RegularExpressions;

namespace Juvin.Core.Rewrite;

public enum DiffKind
{
    Equal,
    Removed,
    Added,
}

public sealed record DiffSegment(DiffKind Kind, string Text);

/// <summary>
///     Word-level diff over tokens that are words or whitespace runs, so concatenating the Equal
///     and Removed segments gives the original text and Equal and Added gives the revised text.
/// </summary>
public static partial class TextDiff
{
    // Past this many table cells the middle is reported as one removal and one addition.
    private const long MaxCells = 4_000_000;

    public static IReadOnlyList<DiffSegment> Compute(string original, string revised)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(revised);

        var a = Tokenize(original);
        var b = Tokenize(revised);
        var segments = new SegmentBuilder();

        var prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix])
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[^(suffix + 1)] == b[^(suffix + 1)])
        {
            suffix++;
        }

        for (var i = 0; i < prefix; i++)
        {
            segments.Add(DiffKind.Equal, a[i]);
        }

        DiffMiddle(a[prefix..^suffix], b[prefix..^suffix], segments);

        for (var i = a.Length - suffix; i < a.Length; i++)
        {
            segments.Add(DiffKind.Equal, a[i]);
        }

        return Coalesce(segments.Build());
    }

    /// <summary>
    ///     Turns each stretch of changes, including the whitespace shared between its words, into
    ///     one removal followed by one addition, which reads far better than alternating words.
    /// </summary>
    private static List<DiffSegment> Coalesce(List<DiffSegment> segments)
    {
        var result = new List<DiffSegment>();
        var i = 0;
        while (i < segments.Count)
        {
            if (segments[i].Kind == DiffKind.Equal)
            {
                result.Add(segments[i++]);
                continue;
            }

            var removed = new StringBuilder();
            var added = new StringBuilder();
            for (; i < segments.Count; i++)
            {
                var segment = segments[i];
                if (segment.Kind == DiffKind.Equal)
                {
                    var bridges = i + 1 < segments.Count && string.IsNullOrWhiteSpace(segment.Text);
                    if (!bridges)
                    {
                        break;
                    }

                    removed.Append(segment.Text);
                    added.Append(segment.Text);
                }
                else
                {
                    (segment.Kind == DiffKind.Removed ? removed : added).Append(segment.Text);
                }
            }

            if (removed.Length > 0)
            {
                result.Add(new DiffSegment(DiffKind.Removed, removed.ToString()));
            }

            if (added.Length > 0)
            {
                result.Add(new DiffSegment(DiffKind.Added, added.ToString()));
            }
        }

        return result;
    }

    private static void DiffMiddle(string[] a, string[] b, SegmentBuilder segments)
    {
        var n = a.Length;
        var m = b.Length;
        if (n == 0 || m == 0 || (long)(n + 1) * (m + 1) > MaxCells)
        {
            segments.Add(DiffKind.Removed, string.Concat(a));
            segments.Add(DiffKind.Added, string.Concat(b));
            return;
        }

        // lcs[i, j] is the length of the longest common subsequence of a[i..] and b[j..].
        var lcs = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (a[x] == b[y])
            {
                segments.Add(DiffKind.Equal, a[x++]);
                y++;
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1])
            {
                segments.Add(DiffKind.Removed, a[x++]);
            }
            else
            {
                segments.Add(DiffKind.Added, b[y++]);
            }
        }

        while (x < n)
        {
            segments.Add(DiffKind.Removed, a[x++]);
        }

        while (y < m)
        {
            segments.Add(DiffKind.Added, b[y++]);
        }
    }

    private static string[] Tokenize(string text) =>
        [.. TokenPattern().Matches(text).Select(match => match.Value)];

    [GeneratedRegex(@"\s+|\S+")]
    private static partial Regex TokenPattern();

    /// <summary>Merges consecutive tokens of the same kind into one segment.</summary>
    private sealed class SegmentBuilder
    {
        private readonly StringBuilder _pending = new();
        private readonly List<DiffSegment> _segments = [];
        private DiffKind _kind;

        public void Add(DiffKind kind, string text)
        {
            if (text.Length == 0)
            {
                return;
            }

            if (kind != _kind)
            {
                Flush();
                _kind = kind;
            }

            _pending.Append(text);
        }

        public List<DiffSegment> Build()
        {
            Flush();
            return _segments;
        }

        private void Flush()
        {
            if (_pending.Length == 0)
            {
                return;
            }

            _segments.Add(new DiffSegment(_kind, _pending.ToString()));
            _pending.Clear();
        }
    }
}
