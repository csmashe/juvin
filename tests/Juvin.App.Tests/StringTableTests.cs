using System.Text.RegularExpressions;
using Juvin.Core.Localization;

namespace Juvin.App.Tests;

public sealed partial class StringTableTests
{
    [Fact]
    public void EveryKeyTheAppUses_IsInTheEnglishCatalog()
    {
        var localizer = new Localizer();
        var appSource = Path.Join(FindRepositoryRoot(), "src", "Juvin.App");
        var keys = Directory
            .EnumerateFiles(appSource, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".axaml", StringComparison.Ordinal) || path.EndsWith(".cs", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(path => KeyPattern().Matches(File.ReadAllText(path)))
            .Select(match => match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(keys);
        Assert.All(keys, key => Assert.NotEqual(key, localizer[key]));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "Juvin.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Juvin.slnx not found.");
    }

    // {loc:Str Some.Key} in XAML, or a quoted "Section.Key" literal in C#.
    // ReSharper disable once UseRawString -- the pattern ends with a quote, which a raw string cannot hold
    [GeneratedRegex(@"\{loc:Str ([A-Za-z]+\.[A-Za-z.]+)\}|""((?:App|Common|Nav|Tray|Wizard|Setup|Home|Shortcuts|General|Models|Provider|BrowserConnector|Panel|RewriteActions)\.[A-Za-z.]+)""")]
    private static partial Regex KeyPattern();
}
