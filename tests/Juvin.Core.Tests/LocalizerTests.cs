using Juvin.Core.Localization;

namespace Juvin.Core.Tests;

public sealed class LocalizerTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public LocalizerTests()
    {
        File.WriteAllText(
            _temp.File("en.json"),
            """{ "Greeting": "Hello", "OnlyEnglish": "English", "Count": "{0} items" }"""
        );
        File.WriteAllText(_temp.File("de.json"), """{ "Greeting": "Hallo" }""");
    }

    public void Dispose()
    {
        _temp.Dispose();
    }

    [Fact]
    public void Indexer_FallsBackFromCurrentLanguageToEnglishToKey()
    {
        var localizer = new Localizer(_temp.Path);
        localizer.SetLanguage("de");

        Assert.Equal("Hallo", localizer["Greeting"]);
        Assert.Equal("English", localizer["OnlyEnglish"]);
        Assert.Equal("Missing.Key", localizer["Missing.Key"]);
    }

    [Fact]
    public void Format_SubstitutesArguments()
    {
        var localizer = new Localizer(_temp.Path);

        Assert.Equal("3 items", localizer.Format("Count", 3));
    }

    [Theory]
    [InlineData("de", "de")]
    [InlineData("DE", "de")]
    [InlineData("de-AT", "de")]
    [InlineData("fr", "en")]
    public void ResolveLanguage_MapsToAnAvailableCatalog(string setting, string expected)
    {
        var localizer = new Localizer(_temp.Path);

        Assert.Equal(expected, localizer.ResolveLanguage(setting));
    }

    [Fact]
    public void SetLanguage_RaisesLanguageChangedOnlyOnChange()
    {
        var localizer = new Localizer(_temp.Path);
        var raised = new List<string>();
        localizer.LanguageChanged += raised.Add;

        localizer.SetLanguage("en");
        localizer.SetLanguage("de");
        localizer.SetLanguage("de");

        Assert.Equal(["de"], raised);
        Assert.Equal("de", localizer.CurrentLanguage);
    }

    [Fact]
    public void Indexer_ReturnsKeyWhenCatalogDirectoryIsMissing()
    {
        var localizer = new Localizer(_temp.File("does-not-exist"));

        Assert.Equal("Greeting", localizer["Greeting"]);
    }
}
