using Juvin.Core.Providers;

namespace Juvin.Platform.Mac;

/// <summary>
///     Finder and launchd start the app with only <c>/usr/bin:/bin:/usr/sbin:/sbin</c>: the Codex
///     installer's own directory, so its binary wins over a shim of the same name, Homebrew on Apple
///     silicon and Intel, then the per-user npm, bun, and local bin directories.
/// </summary>
public sealed class MacToolSearchPaths : IToolSearchPaths
{
    private readonly IReadOnlyList<string> _directories =
        For(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), CodexStandaloneInstall.CurrentBinDirectory());

    public IReadOnlyList<string> GetDirectories() => _directories;

    internal static IReadOnlyList<string> For(string home, string? codexBin)
    {
        string[] system = ["/opt/homebrew/bin", "/usr/local/bin"];
        string[] perUser = string.IsNullOrEmpty(home)
            ? []
            : [Path.Join(home, ".npm-global", "bin"), Path.Join(home, ".bun", "bin"), Path.Join(home, ".local", "bin")];
        return codexBin is null ? [.. system, .. perUser] : [codexBin, .. system, .. perUser];
    }
}
