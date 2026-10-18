namespace Juvin.Platform;

/// <summary>
///     Directories where command-line tools such as the Codex CLI and node are commonly installed but
///     that are missing from PATH when the desktop, not a terminal, started the app.
/// </summary>
public interface IToolSearchPaths
{
    /// <summary>Fully qualified directories, searched after PATH and appended to a child's PATH.</summary>
    IReadOnlyList<string> Directories { get; }
}
