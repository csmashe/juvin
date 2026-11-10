namespace Juvin.Core.Providers;

/// <summary>
///     Codex's standalone installer keeps the real binary in <c>packages/standalone/current/bin</c> under
///     <c>CODEX_HOME</c>, <c>~/.codex</c> by default, and points the command it puts on PATH at it.
/// </summary>
public static class CodexStandaloneInstall
{
    private static readonly string s_binUnderCodexHome = Path.Join("packages", "standalone", "current", "bin");

    /// <summary>Null when neither <paramref name="codexHome" /> nor <paramref name="home" /> is known.</summary>
    public static string? BinDirectory(string home, string? codexHome)
    {
        if (!string.IsNullOrWhiteSpace(codexHome))
        {
            return Path.Join(codexHome, s_binUnderCodexHome);
        }

        return string.IsNullOrEmpty(home) ? null : Path.Join(home, ".codex", s_binUnderCodexHome);
    }

    /// <summary>The directory for the current user and environment.</summary>
    public static string? CurrentBinDirectory() =>
        BinDirectory(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetEnvironmentVariable("CODEX_HOME"));
}
