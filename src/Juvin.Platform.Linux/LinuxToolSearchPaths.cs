namespace Juvin.Platform.Linux;

/// <summary>
///     A desktop session's PATH comes from the login environment, not the shell profile that usually adds
///     the per-user npm, bun, and local bin directories.
/// </summary>
public sealed class LinuxToolSearchPaths : IToolSearchPaths
{
    public IReadOnlyList<string> Directories { get; } = For(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    internal static IReadOnlyList<string> For(string home) =>
        string.IsNullOrEmpty(home)
            ? []
            : [Path.Join(home, ".local", "bin"), Path.Join(home, ".npm-global", "bin"), Path.Join(home, ".bun", "bin")];
}
