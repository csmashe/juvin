using System.Runtime.Versioning;
using Juvin.Core.Hotkeys;
using Juvin.Platform.Linux.Hotkeys.DesktopShortcut;

namespace Juvin.Platform.Tests;

/// <summary>A desktop session rooted in a temp directory, with chosen environment variables and tools on the path.</summary>
[SupportedOSPlatform("linux")]
internal sealed class DesktopShortcutTestSession : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly Dictionary<string, string> _environment = new(StringComparer.Ordinal);

    public DesktopShortcutTestSession()
    {
        Directory.CreateDirectory(Bin);
    }

    public string ConfigHome => _temp.File("config");

    public string DataHome => _temp.File("data");

    private string Bin => _temp.File("bin");

    public DesktopSession Session => new(ConfigHome, DataHome, [Bin], name => _environment.GetValueOrDefault(name));

    public static DesktopShortcutSpec Spec(string chord = "Ctrl+Shift+F", string command = "/opt/juvin/juvin --trigger")
    {
        Assert.True(KeyChord.TryParse(chord, out var parsed));
        return new DesktopShortcutSpec("juvin.rewrite", "Juvin: Rewrite draft", parsed, command);
    }

    public void With(string name, string value)
    {
        _environment[name] = value;
    }

    public void WithTool(string name)
    {
        File.WriteAllText(Path.Join(Bin, name), "");
    }

    public string WriteConfig(string relative, string contents)
    {
        var path = Path.Join(ConfigHome, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    public void Dispose() => _temp.Dispose();
}
