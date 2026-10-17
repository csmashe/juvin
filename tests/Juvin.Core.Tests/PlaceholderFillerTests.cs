using Juvin.Core.Rewrite;

namespace Juvin.Core.Tests;

public sealed class PlaceholderFillerTests
{
    [Theory]
    [InlineData("Best,\n[Your Name]")]
    [InlineData("Best,\n[your name]")]
    [InlineData("Best,\n[ Your  Name ]")]
    [InlineData("Best,\n{Your Name}")]
    [InlineData("Best,\n{{Sender}}")]
    [InlineData("Best,\n[Sender Name]")]
    [InlineData("Best,\n[Signature]")]
    [InlineData("Best,\n[Your Signature]")]
    [InlineData("Best,\n[Your Full Name]")]
    [InlineData("Best,\n[Your Name]  ")]
    public void Fill_ReplacesASignOffPlaceholder(string text)
    {
        Assert.StartsWith("Best,\nChris", PlaceholderFiller.Fill(text, " Chris "));
    }

    [Fact]
    public void Fill_KeepsTheLineLeadAndTheLinesAround()
    {
        const string text = "Thanks,\r\n-- [Your Name]\r\n[Your Title]\r\n";

        Assert.Equal("Thanks,\r\n-- Chris\r\n[Your Title]\r\n", PlaceholderFiller.Fill(text, "Chris"));
    }

    [Fact]
    public void Fill_WorksOnMarkdownAndLeavesAFormattedPlaceholder()
    {
        Assert.Equal("**Thanks**,\nChris", PlaceholderFiller.Fill("**Thanks**,\n[Your Name]", "Chris"));
        Assert.Equal("Thanks,\n**[Your Name]**", PlaceholderFiller.Fill("Thanks,\n**[Your Name]**", "Chris"));
    }

    [Theory]
    [InlineData("See [Attachment] at [2 pm].")]
    [InlineData("Please enter [Your Name] in the form.")]
    [InlineData("Dear [Name],")]
    [InlineData("[Name]")]
    [InlineData("Use <your name> in the payload.")]
    [InlineData("Your name is on the list (name withheld).")]
    [InlineData("[Your Name")]
    public void Fill_LeavesOtherTextAlone(string text)
    {
        Assert.Equal(text, PlaceholderFiller.Fill(text, "Chris"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public void Fill_WithoutAName_LeavesPlaceholders(string? name)
    {
        Assert.Equal("Best,\n[Your Name]", PlaceholderFiller.Fill("Best,\n[Your Name]", name));
    }

    [Fact]
    public void Fill_ReplacesEverySignOffLine()
    {
        Assert.Equal("Chris\nChris\n\nChris", PlaceholderFiller.Fill("[Sender]\n[Your Name]\n\n{Your Name}", "Chris"));
    }

    [Fact]
    public void Fill_InsertsTheNameLiterally()
    {
        Assert.Equal("Best,\n$1 Chris", PlaceholderFiller.Fill("Best,\n[Your Name]", "$1 Chris"));
    }

    [Theory]
    [InlineData("Best,\n[Your Na", "Best,\n")]
    [InlineData("Best,\n{{Sender}", "Best,\n")]
    [InlineData("Best,\n[Your Name]", "Best,\n")]
    [InlineData("Best,\n[Your Name]\n", "Best,\n")]
    [InlineData("Best,\n[Your Name]\u00a0\n", "Best,\n")]
    [InlineData("Best,\n[Your Name]\nTitle", "Best,\nChris\nTitle")]
    [InlineData("See you Friday. \n\t", "See you Friday.")]
    [InlineData("Best,\n  -- ", "Best,\n")]
    [InlineData("Hi there, a long paragraph still being written", "Hi there, a long paragraph still being written")]
    [InlineData("Best,\nChris [Your Name", "Best,\nChris [Your Name")]
    public void FillStreaming_HoldsBackOnlyALineThatCouldBecomeAPlaceholder(string partial, string expected)
    {
        Assert.Equal(expected, PlaceholderFiller.FillStreaming(partial, "Chris"));
    }

    [Theory]
    [InlineData("Hi,\n\nSee you Friday.\n\nBest,\n[Your Name]\n[Your Title]")]
    [InlineData("Enter [Your Name]!\n{{Sender}}\n[Your Name] (sender)\n{{Sender}")]
    [InlineData("One long paragraph with [Your Name] inside it and no line break at all, signed -- [Your Name]")]
    [InlineData("Thanks,\r\n-- [Your Name]\r\n")]
    [InlineData("Best,\n[Your Name]\u00a0\n")]
    [InlineData("See you Friday.  \n\nBest,\nChris \n")]
    public void FillStreaming_PreviewsArePrefixesOfTheFilledResult(string reply)
    {
        // Providers trim the final text before it is filled.
        var final = PlaceholderFiller.Fill(reply.TrimEnd(), "Chris");

        for (var length = 0; length <= reply.Length; length++)
        {
            Assert.StartsWith(PlaceholderFiller.FillStreaming(reply[..length], "Chris"), final);
        }
    }

    [Fact]
    public void FillStreaming_WithoutAName_PassesTextThrough()
    {
        Assert.Equal("Best,\n[Your Na", PlaceholderFiller.FillStreaming("Best,\n[Your Na", null));
    }

    [Fact]
    public void FillStreaming_WithoutAName_DropsTrailingWhitespace()
    {
        Assert.Equal("Best,", PlaceholderFiller.FillStreaming("Best, \n", null));
    }
}
