using Juvin.Core.Rewrite;

namespace Juvin.Core.Tests;

public sealed class TextDiffTests
{
    [Fact]
    public void IdenticalTexts_AreOneEqualRun()
    {
        var diff = TextDiff.Compute("Hi team, see you Friday.", "Hi team, see you Friday.");

        Assert.Equal([new DiffSegment(DiffKind.Equal, "Hi team, see you Friday.")], diff);
    }

    [Fact]
    public void AnInsertedWord_IsAdded()
    {
        var diff = TextDiff.Compute("see you Friday", "see you on Friday");

        Assert.Equal(
            [
                new DiffSegment(DiffKind.Equal, "see you "),
                new DiffSegment(DiffKind.Added, "on "),
                new DiffSegment(DiffKind.Equal, "Friday"),
            ],
            diff
        );
    }

    [Fact]
    public void ADeletedWord_IsRemoved()
    {
        var diff = TextDiff.Compute("see you on Friday", "see you Friday");

        Assert.Equal(
            [
                new DiffSegment(DiffKind.Equal, "see you "),
                new DiffSegment(DiffKind.Removed, "on "),
                new DiffSegment(DiffKind.Equal, "Friday"),
            ],
            diff
        );
    }

    [Fact]
    public void AReplacedWord_IsRemovedThenAdded()
    {
        var diff = TextDiff.Compute("see you Friday", "see you Monday");

        Assert.Equal(
            [
                new DiffSegment(DiffKind.Equal, "see you "),
                new DiffSegment(DiffKind.Removed, "Friday"),
                new DiffSegment(DiffKind.Added, "Monday"),
            ],
            diff
        );
    }

    [Fact]
    public void ARewrittenPhrase_IsOneRemovalAndOneAddition()
    {
        var diff = TextDiff.Compute("hey robin, april is mostly fine", "Hi Robin, April is fine");

        Assert.Equal(
            [
                new DiffSegment(DiffKind.Removed, "hey robin, april"),
                new DiffSegment(DiffKind.Added, "Hi Robin, April"),
                new DiffSegment(DiffKind.Equal, " is"),
                new DiffSegment(DiffKind.Removed, " mostly"),
                new DiffSegment(DiffKind.Equal, " fine"),
            ],
            diff
        );
    }

    [Fact]
    public void EmptyInputs_GiveOnlyTheOtherSide()
    {
        Assert.Empty(TextDiff.Compute("", ""));
        Assert.Equal([new DiffSegment(DiffKind.Added, "new text")], TextDiff.Compute("", "new text"));
        Assert.Equal([new DiffSegment(DiffKind.Removed, "old text")], TextDiff.Compute("old text", ""));
    }

    [Theory]
    [InlineData("Hi Dana,\n\nI can send it by Friday.\nThanks", "Hello Dana,\n\nI will send the report by Friday at noon.\n\nThanks!")]
    [InlineData("one two three four five", "five four three two one")]
    [InlineData("  leading and trailing  ", "leading\tand trailing")]
    [InlineData("a b a b a b", "b a b a")]
    [InlineData("same", "same")]
    public void Segments_RebuildBothTexts(string original, string revised)
    {
        var diff = TextDiff.Compute(original, revised);

        Assert.Equal(original, string.Concat(diff.Where(s => s.Kind != DiffKind.Added).Select(s => s.Text)));
        Assert.Equal(revised, string.Concat(diff.Where(s => s.Kind != DiffKind.Removed).Select(s => s.Text)));
        Assert.All(diff.Zip(diff.Skip(1)), pair => Assert.NotEqual(pair.First.Kind, pair.Second.Kind));
    }
}
