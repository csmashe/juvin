using Juvin.Core.Models;
using Juvin.Core.Providers;
using Juvin.Core.Tests.Fakes;

namespace Juvin.Core.Tests;

public sealed class CodexCliProviderTests : IDisposable
{
    private static readonly string s_fixture = File.ReadAllText(
        Path.Join(AppContext.BaseDirectory, "Fixtures", "codex-events.jsonl")
    );

    private static readonly ChatRequest s_request = new()
    {
        SystemPrompt = "Rewrite the draft.",
        Messages =
        [
            new ChatMessage(ChatRole.User, "<draft>hi dana</draft>"),
            new ChatMessage(ChatRole.Assistant, "Hi Dana,"),
            new ChatMessage(ChatRole.User, "Instruction: shorter"),
        ],
        Model = "gpt-6.1-sol",
    };

    private const string ExecHelp =
        "Options:\n  --json\n  --ephemeral\n  --sandbox <SANDBOX_MODE>\n  --skip-git-repo-check\n  -m, --model <MODEL>\n";

    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        _temp.Dispose();
    }

    [Fact]
    public void ParseEvents_ReadsTheAgentMessageAndUsageFromTheSpikeFixture()
    {
        var output = CodexCliProvider.ParseEvents(s_fixture);

        Assert.StartsWith("Hi Dana,\n\nSorry for the delay.", output.Text);
        Assert.Equal(14490, output.InputTokens);
        Assert.Equal(145, output.OutputTokens);
        Assert.Null(output.Error);
    }

    [Fact]
    public async Task Complete_RunsCodexReadOnlyAndEphemeralWithThePromptOnStdin()
    {
        var runner = new FakeProcessRunner(_ => new ProcessResult(0, s_fixture, ""));
        var preview = new List<string>();

        var result = await Provider(runner)
            .CompleteAsync(s_request, new Rewrite.InlineProgress<string>(preview.Add), CancellationToken.None);

        var call = Assert.Single(runner.Calls);
        Assert.Equal("/fake/codex", call.FileName);
        Assert.Equal(
            ["exec", "--json", "--ephemeral", "--sandbox", "read-only", "--skip-git-repo-check", "-m", "gpt-6.1-sol", "-"],
            call.Arguments
        );
        Assert.Contains("Rewrite the draft.", call.Input);
        Assert.Contains("User:\n<draft>hi dana</draft>", call.Input);
        Assert.Contains("Assistant:\nHi Dana,", call.Input);
        Assert.NotNull(call.WorkingDirectory);
        Assert.False(Directory.Exists(call.WorkingDirectory));
        Assert.EndsWith("Best,", result.Text);
        Assert.Equal(145, result.OutputTokens);
        Assert.Equal("gpt-6.1-sol", result.Model);
        Assert.Equal([result.Text], preview);
    }

    [Fact]
    public async Task Complete_OmitsTheModelFlagWhenNoneIsSet()
    {
        var runner = new FakeProcessRunner(_ => new ProcessResult(0, s_fixture, ""));

        var result = await Provider(runner).CompleteAsync(s_request with { Model = null }, null, CancellationToken.None);

        Assert.DoesNotContain("-m", runner.Calls[0].Arguments);
        Assert.Equal("default", result.Model);
    }

    [Fact]
    public async Task Stream_YieldsTheWholeReplyOnce()
    {
        var provider = Provider(new FakeProcessRunner(_ => new ProcessResult(0, s_fixture, "")));
        var deltas = new List<string>();

        await foreach (var delta in provider.StreamAsync(s_request, CancellationToken.None))
        {
            deltas.Add(delta);
        }

        Assert.Single(deltas);
        Assert.False(provider.SupportsStreaming);
    }

    [Fact]
    public async Task Complete_NonZeroExitReportsTheExitCodeAndCodexError()
    {
        const string failed =
            """
            {"type":"thread.started","thread_id":"t"}
            {"type":"turn.failed","error":{"message":"{\"type\":\"error\",\"status\":400,\"error\":{\"type\":\"invalid_request_error\",\"message\":\"The 'x' model is not supported.\"}}"}}
            """;
        var runner = new FakeProcessRunner(_ => new ProcessResult(1, failed, ""));

        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            Provider(runner).CompleteAsync(s_request, null, CancellationToken.None)
        );

        Assert.Equal(ProviderFailureKind.ProcessFailed, ex.Kind);
        Assert.Equal("Codex exited with code 1. Codex does not accept the model 'gpt-6.1-sol'.", ex.Message);
    }

    [Fact]
    public async Task Complete_NoAgentMessageFailsEvenOnExitZero()
    {
        var runner = new FakeProcessRunner(_ => new ProcessResult(0, "{\"type\":\"turn.started\"}\n", ""));

        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            Provider(runner).CompleteAsync(s_request, null, CancellationToken.None)
        );

        Assert.Equal(ProviderFailureKind.ProcessFailed, ex.Kind);
        Assert.Equal("Codex finished without a reply.", ex.Message);
    }

    [Fact]
    public async Task Complete_NeverEchoesDiagnosticsThatCouldQuoteThePrompt()
    {
        var runner = new FakeProcessRunner(_ => new ProcessResult(2, "", "Invalid input: <draft>hi dana</draft>\nmore"));

        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            Provider(runner).CompleteAsync(s_request, null, CancellationToken.None)
        );

        Assert.Equal("Codex exited with code 2. Codex reported an error; run it from a terminal for details.", ex.Message);
        Assert.DoesNotContain("dana", ex.Message);
    }

    [Fact]
    public async Task Complete_ClassifiesSignInAndUsageLimitErrors()
    {
        var signedOut = new FakeProcessRunner(_ => new ProcessResult(1, "", "Error: not logged in, run codex login"));
        var limited = new FakeProcessRunner(_ => new ProcessResult(1, "", "Error: 429 usage limit reached"));

        var signedOutError = await Assert.ThrowsAsync<ProviderException>(() =>
            Provider(signedOut).CompleteAsync(s_request, null, CancellationToken.None)
        );
        var limitedError = await Assert.ThrowsAsync<ProviderException>(() =>
            Provider(limited).CompleteAsync(s_request, null, CancellationToken.None)
        );

        Assert.Equal("Codex exited with code 1. Codex is not signed in. Run codex login.", signedOutError.Message);
        Assert.Equal("Codex exited with code 1. Codex reached its usage limit.", limitedError.Message);
    }

    [Fact]
    public async Task Complete_WithoutABinaryIsAConfigurationFailure()
    {
        var provider = new CodexCliProvider(new AppSettings(), new FakeProcessRunner(_ => throw new InvalidOperationException()), null, TimeSpan.FromMinutes(1));

        var ex = await Assert.ThrowsAsync<ProviderException>(() => provider.CompleteAsync(s_request, null, CancellationToken.None));

        Assert.Equal(ProviderFailureKind.Configuration, ex.Kind);
    }

    [Theory]
    [InlineData(0, "", "Logged in using ChatGPT", ProviderStatusKind.Ready)]
    [InlineData(0, "Logged in using an API key", "", ProviderStatusKind.Ready)]
    [InlineData(1, "", "Not logged in", ProviderStatusKind.NotSignedIn)]
    public async Task Probe_ReadsTheSignInState(int exitCode, string stdout, string stderr, ProviderStatusKind expected)
    {
        var runner = new FakeProcessRunner(args =>
            args[0] switch
            {
                "--version" => new ProcessResult(0, "codex-cli 0.162.0\n", ""),
                "exec" => new ProcessResult(0, ExecHelp, ""),
                _ => new ProcessResult(exitCode, stdout, stderr),
            }
        );

        var status = await Provider(runner).ProbeAsync(CancellationToken.None);

        Assert.Equal(expected, status.Kind);
        Assert.Equal("codex-cli 0.162.0", status.Version);
    }

    [Fact]
    public async Task Probe_ReportsACodexWithoutTheNeededFlagsAsIncompatible()
    {
        var runner = new FakeProcessRunner(args =>
            args[0] switch
            {
                "--version" => new ProcessResult(0, "codex-cli 0.40.0\n", ""),
                "exec" => new ProcessResult(0, "Options:\n  --json\n  --sandbox <MODE>\n", ""),
                _ => new ProcessResult(0, "", "Logged in using ChatGPT"),
            }
        );

        var status = await Provider(runner).ProbeAsync(CancellationToken.None);

        Assert.Equal(ProviderStatusKind.Incompatible, status.Kind);
        Assert.Equal("codex-cli 0.40.0", status.Version);
        Assert.DoesNotContain(runner.Calls, call => call.Arguments[0] == "login");
    }

    [Fact]
    public async Task Probe_WithoutABinaryIsNotInstalled()
    {
        var provider = new CodexCliProvider(new AppSettings(), new FakeProcessRunner(_ => throw new InvalidOperationException()), null, TimeSpan.FromMinutes(1));

        Assert.Equal(ProviderStatusKind.NotInstalled, (await provider.ProbeAsync(CancellationToken.None)).Kind);
    }

    [Fact]
    public void ResolveBinary_SearchesPathAndPrefersAConfiguredFile()
    {
        var first = Directory.CreateDirectory(_temp.File("a")).FullName;
        var second = Directory.CreateDirectory(_temp.File("b")).FullName;
        File.WriteAllText(Path.Join(second, "codex"), "");
        File.WriteAllText(Path.Join(second, "codex.cmd"), "");
        var path = string.Join(Path.PathSeparator, first, second);

        Assert.Equal(Path.Join(second, "codex"), CodexCliProvider.ResolveBinary(null, path, isWindows: false));
        Assert.Equal(Path.Join(second, "codex.cmd"), CodexCliProvider.ResolveBinary(null, path, isWindows: true));
        Assert.Null(CodexCliProvider.ResolveBinary(null, first, isWindows: false));
        Assert.Null(CodexCliProvider.ResolveBinary(_temp.File("missing"), path, isWindows: false));
        var configured = Path.Join(second, "codex");
        Assert.Equal(configured, CodexCliProvider.ResolveBinary(configured, "", isWindows: false));
        var relative = Path.GetRelativePath(Environment.CurrentDirectory, configured);
        Assert.Equal(configured, CodexCliProvider.ResolveBinary(relative, "", isWindows: false));
    }

    private static CodexCliProvider Provider(FakeProcessRunner runner) =>
        new(new AppSettings(), runner, "/fake/codex", TimeSpan.FromMinutes(1));

    [Fact]
    public void BuildStartInfo_RunsAWindowsBatchShimThroughTheInterpreter()
    {
        var info = ProcessRunner.BuildStartInfo(@"C:\npm\codex.cmd", ["exec", "-m", "gpt-6.1-sol", "-"], isWindows: true);

        Assert.EndsWith("cmd.exe", info.FileName, StringComparison.OrdinalIgnoreCase);
        // ReSharper disable once UseRawString -- a raw string cannot end with a quote character
        Assert.Equal(@"/d /s /c ""C:\npm\codex.cmd exec -m gpt-6.1-sol -""", info.Arguments);
        Assert.Empty(info.ArgumentList);
    }

    [Fact]
    public void BuildStartInfo_QuotesPathsWithSpacesAndRejectsShellCharacters()
    {
        var info = ProcessRunner.BuildStartInfo(@"C:\Program Files\codex.cmd", ["--version"], isWindows: true);

        // ReSharper disable once UseRawString -- a raw string cannot end with a quote character
        Assert.Equal(@"/d /s /c """"C:\Program Files\codex.cmd"" --version""", info.Arguments);
        Assert.Throws<ArgumentException>(() => ProcessRunner.BuildStartInfo(@"C:\npm\codex.cmd", ["-m", "x&calc"], isWindows: true));
    }

    [Fact]
    public void BuildStartInfo_StartsExecutablesDirectly()
    {
        var info = ProcessRunner.BuildStartInfo(@"C:\npm\codex.exe", ["--version"], isWindows: true);
        var unix = ProcessRunner.BuildStartInfo("/usr/bin/codex.cmd", ["--version"], isWindows: false);

        Assert.Equal(@"C:\npm\codex.exe", info.FileName);
        Assert.Equal(["--version"], info.ArgumentList);
        Assert.Equal("/usr/bin/codex.cmd", unix.FileName);
    }

    [Fact]
    public async Task ArgumentsTheShellCannotTake_AreAConfigurationFailureAndANotInstalledProbe()
    {
        var runner = new FakeProcessRunner(_ => throw new ArgumentException("bad"));
        var provider = Provider(runner);

        var ex = await Assert.ThrowsAsync<ProviderException>(() => provider.CompleteAsync(s_request, null, CancellationToken.None));

        Assert.Equal(ProviderFailureKind.Configuration, ex.Kind);
        Assert.Equal(ProviderStatusKind.NotInstalled, (await provider.ProbeAsync(CancellationToken.None)).Kind);
    }

    [Fact]
    public void BuildStartInfo_QuotesAnEmptyArgument()
    {
        var info = ProcessRunner.BuildStartInfo(@"C:\npm\codex.cmd", ["-m", "", "-"], isWindows: true);

        Assert.Contains("""-m "" -""", info.Arguments);
    }
}
