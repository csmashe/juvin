namespace Juvin.Core.Tests;

public sealed class LauncherNameTests
{
    [Theory]
    [InlineData("0.1.0-alpha.2", "Juvin (Alpha)")]
    [InlineData("0.1.0-alpha", "Juvin (Alpha)")]
    [InlineData("0.2.0-beta.1+abc123", "Juvin (Beta)")]
    [InlineData("1.0.0-rc.1", "Juvin (RC)")]
    [InlineData("1.0.0", "Juvin")]
    [InlineData("1.0.0+abc-def", "Juvin")]
    [InlineData("", "Juvin")]
    public void For_LabelsThePrereleaseWord(string version, string expected)
    {
        Assert.Equal(expected, LauncherName.For(version));
    }
}
