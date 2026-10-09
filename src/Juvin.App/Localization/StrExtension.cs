using Avalonia.Markup.Xaml;
using Juvin.Core.Localization;

namespace Juvin.App.Localization;

/// <summary>
///     <c>{loc:Str Key}</c> in XAML resolves a string table entry. Strings are read once when the
///     view loads; the language is applied at startup before any window is built.
/// </summary>
public sealed class StrExtension : MarkupExtension
{
    private readonly string _key;

    public StrExtension(string key)
    {
        _key = key;
    }

    /// <summary>Set by the app at startup to the DI singleton. The default serves the previewer.</summary>
    public static Localizer Localizer { get; set; } = new();

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        return Localizer[_key];
    }
}
