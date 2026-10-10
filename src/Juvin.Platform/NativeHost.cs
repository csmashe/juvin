namespace Juvin.Platform;

/// <summary>
///     Identity of the native messaging host and the extensions allowed to launch it. The
///     extension IDs are pinned in <c>extension/src/manifest.json</c>; change both together.
/// </summary>
public static class NativeHost
{
    public const string Name = "com.juvin.host";
    public const string Description = "Juvin browser connector";
    public const string FirefoxExtensionId = "extension@juvin.app";
    private const string ChromeExtensionId = "okfcbcoboinjniofikfnmllkbaeadelo";
    public static string ChromeOrigin => $"chrome-extension://{ChromeExtensionId}/";

    /// <summary>Environment variable that overrides where the host executable is looked for; for development.</summary>
    public const string PathOverrideVariable = "JUVIN_HOST_PATH";

    /// <summary>
    ///     The host executable the manifests must point at: <see cref="PathOverrideVariable" /> when set,
    ///     otherwise <c>juvin-host</c> next to the running app. Null when neither exists.
    /// </summary>
    public static string? Locate()
    {
        var overridePath = Environment.GetEnvironmentVariable(PathOverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return File.Exists(overridePath) ? Path.GetFullPath(overridePath) : null;
        }

        var sibling = Path.Join(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "juvin-host.exe" : "juvin-host");
        return File.Exists(sibling) ? sibling : null;
    }
}
