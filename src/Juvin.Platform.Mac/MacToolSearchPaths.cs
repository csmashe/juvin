namespace Juvin.Platform.Mac;

/// <summary>
///     Finder and launchd start the app with only <c>/usr/bin:/bin:/usr/sbin:/sbin</c>: Homebrew on Apple
///     silicon and Intel, then the per-user npm, bun, and local bin directories.
/// </summary>
public sealed class MacToolSearchPaths : IToolSearchPaths
{
    public IReadOnlyList<string> Directories { get; } = For(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    internal static IReadOnlyList<string> For(string home)
    {
        string[] system = ["/opt/homebrew/bin", "/usr/local/bin"];
        return string.IsNullOrEmpty(home)
            ? system
            : [.. system, Path.Join(home, ".npm-global", "bin"), Path.Join(home, ".bun", "bin"), Path.Join(home, ".local", "bin")];
    }
}
