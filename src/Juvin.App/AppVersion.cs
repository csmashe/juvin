using System.Reflection;

namespace Juvin.App;

public static class AppVersion
{
    public static string Current { get; } = Read();

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
