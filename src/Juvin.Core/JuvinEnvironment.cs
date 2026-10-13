namespace Juvin.Core;

/// <summary>
///     Per-user data locations. The same folder name is used on every OS; on Linux it resolves to
///     <c>~/.local/share/Juvin</c>.
/// </summary>
public static class JuvinEnvironment
{
    private const UnixFileMode OwnerOnlyDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    public static string DataPath { get; } = Path.Join(
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify
        ),
        "Juvin"
    );

    public static string SettingsFilePath => SettingsFilePathIn(DataPath);

    public static string HistoryFilePath => HistoryFilePathIn(DataPath);

    public static string SecretKeyFilePath => SecretKeyFilePathIn(DataPath);

    public static string SecretsPath => SecretsPathIn(DataPath);

    public static string LogsPath => LogsPathIn(DataPath);

    public static string RuntimePath => RuntimePathIn(DataPath);

    public static void EnsureDirectories()
    {
        EnsureDirectories(DataPath);
    }

    private static string SettingsFilePathIn(string dataPath) => Path.Join(dataPath, "settings.json");

    private static string HistoryFilePathIn(string dataPath) => Path.Join(dataPath, "history.json");

    private static string SecretKeyFilePathIn(string dataPath) => Path.Join(dataPath, "secret.key");

    private static string SecretsPathIn(string dataPath) => Path.Join(dataPath, "Secrets");

    private static string LogsPathIn(string dataPath) => Path.Join(dataPath, "Logs");

    private static string RuntimePathIn(string dataPath) => Path.Join(dataPath, "Runtime");

    internal static void EnsureDirectories(string dataPath)
    {
        foreach (
            var directory in new[]
            {
                dataPath,
                SecretsPathIn(dataPath),
                LogsPathIn(dataPath),
                RuntimePathIn(dataPath),
            }
        )
        {
            CreatePrivateDirectory(directory);
        }
    }

    /// <summary>
    ///     Removes the stored secrets and the key that encrypts them. Only these files hold Juvin
    ///     secrets: the macOS Keychain holds none in the MVP, because no provider needs a key.
    /// </summary>
    public static void DeleteSecretsIn(string dataPath)
    {
        var secrets = SecretsPathIn(dataPath);
        if (Directory.Exists(secrets))
        {
            foreach (var directory in Directory.EnumerateDirectories(secrets))
            {
                Directory.Delete(directory, recursive: true);
            }

            foreach (var file in Directory.EnumerateFiles(secrets))
            {
                File.Delete(file);
            }
        }

        var key = SecretKeyFilePathIn(dataPath);
        if (File.Exists(key))
        {
            File.Delete(key);
        }
    }

    /// <summary>
    ///     Creates <paramref name="directory" /> readable only by the current user. On Unix the mode
    ///     is also applied to a directory that already existed.
    /// </summary>
    public static void CreatePrivateDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
            return;
        }

        Directory.CreateDirectory(directory, OwnerOnlyDirectoryMode);
        File.SetUnixFileMode(directory, OwnerOnlyDirectoryMode);
    }
}
