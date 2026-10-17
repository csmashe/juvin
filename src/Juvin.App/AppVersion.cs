using System.Reflection;

namespace Juvin.App;

public static class AppVersion
{
    public static string Current { get; } = Read();

    /// <summary>True only for a build made from a pushed release tag, whose files exist at that tag.</summary>
    public static bool IsReleaseTag { get; } = bool.TryParse(ReadMetadata("ReleaseTag"), out var tagged) && tagged;

    private static string? ReadMetadata(string key) =>
        typeof(AppVersion).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(attribute => attribute.Key == key)?.Value;

    private static string Read()
    {
        var assembly = typeof(AppVersion).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(informational))
        {
            return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }

        // Drop the "+commit" source revision suffix the SDK appends.
        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? informational : informational[..plus];
    }
}
