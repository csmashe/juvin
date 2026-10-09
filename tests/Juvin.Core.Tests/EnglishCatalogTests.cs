using System.Text.Json;
using Juvin.Core.Localization;

namespace Juvin.Core.Tests;

public sealed class EnglishCatalogTests
{
    private static readonly string s_catalogPath = Path.Join(
        Localizer.DefaultCatalogDirectory,
        "en.json"
    );

    [Fact]
    public void Catalog_HasNoDuplicateKeysAndNoEmptyValues()
    {
        var reader = new Utf8JsonReader(File.ReadAllBytes(s_catalogPath));
        var keys = new HashSet<string>(StringComparer.Ordinal);

        Assert.True(reader.Read());
        Assert.Equal(JsonTokenType.StartObject, reader.TokenType);
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var key = reader.GetString()!;
            Assert.True(keys.Add(key), $"Duplicate key '{key}'.");
            Assert.True(reader.Read());
            Assert.Equal(JsonTokenType.String, reader.TokenType);
            Assert.False(string.IsNullOrWhiteSpace(reader.GetString()), $"Empty value for '{key}'.");
        }

        Assert.NotEmpty(keys);
    }

    [Theory]
    [InlineData("Nav.Home")]
    [InlineData("Common.OpenSetupWizard")]
    [InlineData("Tray.Exit")]
    [InlineData("Wizard.Checklist.Title")]
    [InlineData("Setup.Recommended")]
    public void Catalog_ContainsKeysTheUiUses(string key)
    {
        var localizer = new Localizer();

        Assert.NotEqual(key, localizer[key]);
    }

    [Fact]
    public void Catalog_HasTagline()
    {
        Assert.Equal("Works beside you", new Localizer()["App.Tagline"]);
    }
}
