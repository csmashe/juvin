using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Juvin.App.ViewModels.Sections;

public sealed class AboutSectionViewModel : ObservableObject
{
    public string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var informational = typeof(AboutSectionViewModel)
            .Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (string.IsNullOrEmpty(informational))
        {
            return typeof(AboutSectionViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }

        // Drop the "+commit" source revision suffix the SDK appends.
        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? informational : informational[..plus];
    }
}
