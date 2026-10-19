namespace Juvin.Core.Providers;

internal static class SearchPath
{
    /// <summary>
    ///     <paramref name="path" /> with each fully qualified directory it lacks appended in order, so the
    ///     inherited entries keep their precedence.
    /// </summary>
    public static string Append(string? path, IEnumerable<string> directories)
    {
        var present = new HashSet<string>((path ?? "").Split(Path.PathSeparator), StringComparer.Ordinal);
        var missing = directories.Where(directory => Path.IsPathFullyQualified(directory) && present.Add(directory)).ToList();
        if (missing.Count == 0)
        {
            return path ?? "";
        }

        var appended = string.Join(Path.PathSeparator, missing);
        return string.IsNullOrEmpty(path) ? appended : path + Path.PathSeparator + appended;
    }
}
