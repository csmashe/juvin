namespace Juvin.Platform.Windows;

/// <summary>None: Explorer starts the app with the user's full PATH, which npm's and bun's installers update.</summary>
public sealed class WindowsToolSearchPaths : IToolSearchPaths
{
    public IReadOnlyList<string> Directories => [];
}
