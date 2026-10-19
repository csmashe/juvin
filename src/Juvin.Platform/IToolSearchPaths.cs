namespace Juvin.Platform;

/// <summary>
///     Directories where command-line tools such as the Codex CLI and node are commonly installed but
///     that are missing from PATH when the desktop, not a terminal, started the app, or when the tool
///     was installed after the app started.
/// </summary>
public interface IToolSearchPaths
{
    /// <summary>
    ///     Fully qualified directories, searched after PATH and appended to a child's PATH. Read again on
    ///     each call, so a tool installed while the app runs is found without a restart.
    /// </summary>
    IReadOnlyList<string> GetDirectories();
}
