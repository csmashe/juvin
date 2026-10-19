namespace Juvin.Platform.Linux;

/// <summary>
///     A desktop session's PATH comes from the login environment, not the shell profile that usually adds
///     the per-user npm, bun, and local bin directories.
/// </summary>
public sealed class LinuxToolSearchPaths : IToolSearchPaths
{
    private readonly IReadOnlyList<string> _directories = For(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public IReadOnlyList<string> GetDirectories() => _directories;

    internal static IReadOnlyList<string> For(string home) =>
        string.IsNullOrEmpty(home)
            ? []
            : [Path.Join(home, ".local", "bin"), Path.Join(home, ".npm-global", "bin"), Path.Join(home, ".bun", "bin")];
}
