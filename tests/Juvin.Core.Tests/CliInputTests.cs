using Juvin.Cli;
using Juvin.Core.Models;
using Juvin.Core.Rewrite;

namespace Juvin.Core.Tests;

public sealed class CliInputTests
{
    [Fact]
    public void Stdin_PlainTextIsTheDraftAlone()
    {
        var context = StdinInput.Parse("  hey can u send it  \n");

        Assert.Equal("hey can u send it", context.Draft);
        Assert.Empty(context.Thread);
        Assert.Equal(ThreadCompleteness.DraftOnly, context.Completeness);
    }

    [Fact]
    public void Stdin_ReadsTheThreadShape()
    {
        var context = StdinInput.Parse(
            """
            { "draft": "ok", "subject": "Re: x",
              "thread": [ { "from": "Dana", "to": ["chris@example.com"], "sent": "2026-10-01T09:00:00Z", "body": "Hi" } ] }
            """
        );

        var message = Assert.Single(context.Thread);
        Assert.Equal("Dana", message.From);
        Assert.Equal(["chris@example.com"], message.To);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero), message.Sent);
        Assert.Equal("Re: x", context.Subject);
        Assert.Equal(ThreadCompleteness.EntireThread, context.Completeness);
    }

    [Fact]
    public void Stdin_ReadsTheSpikeSampleShape()
    {
        var context = StdinInput.Parse(
            """
            { "subject": "Re: Q4", "collapsed": 2, "draft": "hi dana",
              "messages": [ { "from": "Dana", "date": "2026-09-29 09:12", "body": "Hello" }, { "from": "Priya", "body": "Hi" } ] }
            """
        );

        Assert.Equal(2, context.Thread.Count);
        Assert.Equal(new DateTimeOffset(2026, 9, 29, 9, 12, 0, TimeSpan.Zero), context.Thread[0].Sent);
        Assert.Null(context.Thread[1].Sent);
        Assert.Equal(2, context.CollapsedCount);
        Assert.Equal(ThreadCompleteness.LoadedMessagesOnly, context.Completeness);
    }

    [Theory]
    [InlineData("{ \"subject\": \"no draft\" }")]
    [InlineData("{ not json")]
    public void Stdin_RejectsBadJson(string input)
    {
        Assert.Throws<InvalidDataException>(() => StdinInput.Parse(input));
    }

    [Fact]
    public void Options_ParseAllFlags()
    {
        var options = CliOptions.Parse(
            ["rewrite", "--stdin", "--action", "missing", "--mode", "codex", "--model", "m", "--server", "http://h", "--provenance", "--settings", "/s.json"],
            out var error
        );

        Assert.Null(error);
        Assert.NotNull(options);
        Assert.Same(RewriteAction.Missing, options.Action);
        Assert.Equal(ProcessingMode.Codex, options.Mode);
        Assert.True(options.Provenance);
        Assert.Equal("/s.json", options.SettingsPath);

        var settings = options.ApplyTo(new AppSettings { LocalModel = "keep" });
        Assert.Equal(ProcessingMode.Codex, settings.ProcessingMode);
        Assert.Equal("m", settings.CodexModel);
        Assert.Equal("keep", settings.LocalModel);
        Assert.Equal("http://h", settings.LocalServerUrl);
        Assert.True(settings.ProvenanceCaptureEnabled);
    }

    [Fact]
    public void Options_RejectABlankInstruction()
    {
        Assert.Null(CliOptions.Parse(["rewrite", "--stdin", "--instruction", "   "], out var error));
        Assert.Contains("--instruction", error);
    }

    [Fact]
    public void Options_InstructionAloneMeansCustom()
    {
        var options = CliOptions.Parse(["rewrite", "--stdin", "--instruction", "Be brief"], out _);

        Assert.Same(RewriteAction.Custom, options?.Action);
        Assert.Equal("Be brief", options?.Instruction);
    }

    [Theory]
    [InlineData("")]
    [InlineData("rewrite")]
    [InlineData("rewrite --stdin --action nope")]
    [InlineData("rewrite --stdin --action custom")]
    [InlineData("rewrite --stdin --mode 99")]
    [InlineData("rewrite --stdin --action soften --instruction x")]
    [InlineData("rewrite --stdin --mode cloud")]
    [InlineData("rewrite --stdin --model")]
    [InlineData("rewrite --stdin --bogus x")]
    [InlineData("translate --stdin")]
    public void Options_ReportUsageErrors(string commandLine)
    {
        Assert.Null(CliOptions.Parse(commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries), out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Options_Help()
    {
        Assert.True(CliOptions.Parse(["--help"], out _)?.Help);
        Assert.True(CliOptions.Parse(["rewrite", "--help"], out _)?.Help);
    }

    [Fact]
    public void Json_TreatsANullThreadAsNoThread()
    {
        var context = StdinInput.Parse("""{"draft":"hi","thread":null}""");

        Assert.Empty(context.Thread);
    }
}
