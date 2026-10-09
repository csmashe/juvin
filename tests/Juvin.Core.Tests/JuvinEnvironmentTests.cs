namespace Juvin.Core.Tests;

public sealed class JuvinEnvironmentTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        _temp.Dispose();
    }

    [Fact]
    public void DataPath_IsJuvinUnderLocalApplicationData()
    {
        var expected = Path.Join(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolderOption.DoNotVerify
            ),
            "Juvin"
        );

        Assert.Equal(expected, JuvinEnvironment.DataPath);
    }

    [Fact]
    public void Paths_AreInsideDataPath()
    {
        var data = JuvinEnvironment.DataPath;

        Assert.Equal(Path.Join(data, "settings.json"), JuvinEnvironment.SettingsFilePath);
        Assert.Equal(Path.Join(data, "secret.key"), JuvinEnvironment.SecretKeyFilePath);
        Assert.Equal(Path.Join(data, "Secrets"), JuvinEnvironment.SecretsPath);
        Assert.Equal(Path.Join(data, "Logs"), JuvinEnvironment.LogsPath);
        Assert.Equal(Path.Join(data, "Runtime"), JuvinEnvironment.RuntimePath);
    }

    [Fact]
    public void EnsureDirectories_CreatesOwnerOnlyDirectories()
    {
        var data = Path.Join(_temp.Path, "Juvin");

        JuvinEnvironment.EnsureDirectories(data);

        foreach (var directory in new[] { data, Path.Join(data, "Secrets"), Path.Join(data, "Logs"), Path.Join(data, "Runtime") })
        {
            Assert.True(Directory.Exists(directory), directory);
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(directory)
                );
            }
        }
    }
}
