namespace Juvin.Platform.Linux;

/// <summary>
///     A desktop session's PATH comes from the login environment, not the shell profile that usually adds
///     the per-user directories: the Codex installer's own, so its binary wins over a shim of the same
///     name, then local bin, npm, and bun.
/// </summary>
public sealed class LinuxToolSearchPaths : IToolSearchPaths
{
    private readonly IReadOnlyList<string> _directories =
        For(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), CodexStandaloneInstall.CurrentBinDirectory());

    public IReadOnlyList<string> GetDirectories() => _directories;

    internal static IReadOnlyList<string> For(string home, string? codexBin)
    {
        string[] perUser = string.IsNullOrEmpty(home)
            ? []
            : [Path.Join(home, ".local", "bin"), Path.Join(home, ".npm-global", "bin"), Path.Join(home, ".bun", "bin")];
        return codexBin is null ? perUser : [codexBin, .. perUser];
    }
}
