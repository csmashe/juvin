using System.Runtime.Versioning;
using Juvin.Platform.Linux;
using Juvin.Platform.Mac;
using Juvin.Platform.Windows;
using Microsoft.Win32;

namespace Juvin.Platform.Tests;

// The directory lists use Unix separators, so they are checked where those apply.
public sealed class ToolSearchPathsTests
{
    [UnixFact]
    public void CodexStandaloneInstall_IsUnderCodexHomeWhenSetElseUnderDotCodex()
    {
        Assert.Equal("/Users/ada/.codex/packages/standalone/current/bin", CodexStandaloneInstall.BinDirectory("/Users/ada", null));
        Assert.Equal("/Users/ada/.codex/packages/standalone/current/bin", CodexStandaloneInstall.BinDirectory("/Users/ada", " "));
        Assert.Equal("/opt/codex/packages/standalone/current/bin", CodexStandaloneInstall.BinDirectory("/Users/ada", "/opt/codex"));
        Assert.Equal("/opt/codex/packages/standalone/current/bin", CodexStandaloneInstall.BinDirectory("", "/opt/codex"));
        Assert.Null(CodexStandaloneInstall.BinDirectory("", null));
    }

    [UnixFact]
    [SupportedOSPlatform("macos")]
    public void Mac_SearchesTheCodexInstallerThenHomebrewThenThePerUserDirectories()
    {
        const string codexBin = "/Users/ada/.codex/packages/standalone/current/bin";
        Assert.Equal(
            [codexBin, "/opt/homebrew/bin", "/usr/local/bin", "/Users/ada/.npm-global/bin", "/Users/ada/.bun/bin", "/Users/ada/.local/bin"],
            MacToolSearchPaths.For("/Users/ada", codexBin)
        );
        Assert.Equal(["/opt/codex/bin", "/opt/homebrew/bin", "/usr/local/bin"], MacToolSearchPaths.For("", "/opt/codex/bin"));
        Assert.Equal(["/opt/homebrew/bin", "/usr/local/bin"], MacToolSearchPaths.For("", null));
    }

    [UnixFact]
    [SupportedOSPlatform("linux")]
    public void Linux_SearchesTheCodexInstallerThenThePerUserDirectories()
    {
        const string codexBin = "/home/ada/.codex/packages/standalone/current/bin";
        Assert.Equal(
            [codexBin, "/home/ada/.local/bin", "/home/ada/.npm-global/bin", "/home/ada/.bun/bin"],
            LinuxToolSearchPaths.For("/home/ada", codexBin)
        );
        Assert.Equal(["/opt/codex/bin"], LinuxToolSearchPaths.For("", "/opt/codex/bin"));
        Assert.Empty(LinuxToolSearchPaths.For("", null));
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public void Windows_CodexStandaloneInstallIsUnderCodexHomeWhenSetElseUnderTheProfile()
    {
        Assert.Equal(@"C:\Users\ada\.codex\packages\standalone\current\bin", CodexStandaloneInstall.BinDirectory(@"C:\Users\ada", null));
        Assert.Equal(@"D:\Codex\packages\standalone\current\bin", CodexStandaloneInstall.BinDirectory(@"C:\Users\ada", @"D:\Codex"));
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public void Windows_InstallDirectoriesAreTheCodexInstallersThenNpms()
    {
        Assert.Equal(
            [@"C:\Users\ada\AppData\Local\Programs\OpenAI\Codex\bin", @"C:\Users\ada\AppData\Roaming\npm"],
            WindowsToolSearchPaths.InstallDirectories(@"C:\Users\ada\AppData\Local", @"C:\Users\ada\AppData\Roaming")
        );
        Assert.Empty(WindowsToolSearchPaths.InstallDirectories("", ""));
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public void Windows_ReadsTheSavedPathAtEachCall_MachineBeforeUser()
    {
        var rootName = @"Software\JuvinTests\" + Guid.NewGuid().ToString("N");
        try
        {
            using var machine = Registry.CurrentUser.CreateSubKey(rootName + @"\machine");
            using var user = Registry.CurrentUser.CreateSubKey(rootName + @"\user");
            using (var key = machine.CreateSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment"))
            {
                key.SetValue("Path", @"C:\Machine;%SystemRoot%\Tools", RegistryValueKind.ExpandString);
            }

            var paths = new WindowsToolSearchPaths(machine, user, @"C:\Local", @"C:\Roaming", @"C:\Codex\bin");
            Assert.Equal([@"C:\Codex\bin", @"C:\Machine", Environment.ExpandEnvironmentVariables(@"%SystemRoot%\Tools"), @"C:\Local\Programs\OpenAI\Codex\bin", @"C:\Roaming\npm"], paths.GetDirectories());

            using (var key = user.CreateSubKey("Environment"))
            {
                key.SetValue("Path", """ "C:\Users\ada\Codex" ;;C:\User""", RegistryValueKind.String);
            }

            Assert.Equal(
                [@"C:\Codex\bin", @"C:\Machine", Environment.ExpandEnvironmentVariables(@"%SystemRoot%\Tools"), @"C:\Users\ada\Codex", @"C:\User", @"C:\Local\Programs\OpenAI\Codex\bin", @"C:\Roaming\npm"],
                paths.GetDirectories()
            );
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(rootName, throwOnMissingSubKey: false);
        }
    }
}
