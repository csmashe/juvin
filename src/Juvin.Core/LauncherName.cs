using System.Reflection;

namespace Juvin.Core;

/// <summary>
///     The name desktop launchers show: "Juvin", with the prerelease word of the version in brackets,
///     so <c>0.1.0-alpha.2</c> shows as "Juvin (Alpha)" and <c>1.0.0</c> as "Juvin". The packaging
///     scripts derive the same name from the version they are given.
/// </summary>
public static class LauncherName
{
    private const string Product = "Juvin";

    public static string Current { get; } =
        For(typeof(LauncherName).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "");

    public static string For(string version)
    {
        var dash = version.IndexOf('-', StringComparison.Ordinal);
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        if (dash < 0 || (plus >= 0 && plus < dash))
        {
            return Product;
        }

        var word = new string(version[(dash + 1)..].TakeWhile(char.IsAsciiLetter).ToArray());
        if (word.Length == 0)
        {
            return Product;
        }

        var label = word.Length <= 2
            ? word.ToUpperInvariant()
            : char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant();
        return $"{Product} ({label})";
    }
}
