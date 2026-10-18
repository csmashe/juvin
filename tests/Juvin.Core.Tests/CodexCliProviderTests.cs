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
        "Options:\n  --disable <FEATURE>\n  --json\n  --ephemeral\n  --sandbox <SANDBOX_MODE>\n  --skip-git-repo-check\n  -m, --model <MODEL>\n";

    // Every wanted feature but hooks, plus one Juvin leaves alone.
    private const string FeaturesList =
        """
        apps                                     stable             true
        browser_use                              stable             true
        computer_use                             stable             true
        fast_mode                                stable             true
        in_app_browser                           stable             true
        multi_agent                              stable             true
        plugins                                  stable             true
        shell_tool                               stable             true
        unified_exec                             stable             true
        view_image                               stable             true
        """;

    private const string McpList =
        """[{"name":"docs","enabled":true,"transport":{"type":"stdio"}},{"name":"chrome-devtools","enabled":false}]""";

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
        var runner = Codex(_ => new ProcessResult(0, s_fixture, ""));
        var preview = new List<string>();

        var result = await Provider(runner)
            .CompleteAsync(s_request, new Rewrite.InlineProgress<string>(preview.Add), CancellationToken.None);

        var call = Assert.Single(runner.Calls, c => c.Arguments[0] == "exec");
        Assert.Equal("/fake/codex", call.FileName);
        Assert.Equal(
            [
                "exec", "--json", "--ephemeral", "--sandbox", "read-only", "--skip-git-repo-check",
                "-c", "mcp_servers.docs.enabled=false", "-c", "mcp_servers.chrome-devtools.enabled=false",
                "--disable", "shell_tool", "--disable", "unified_exec", "--disable", "browser_use", "--disable", "computer_use",
                "--disable", "in_app_browser", "--disable", "view_image", "--disable", "multi_agent", "--disable", "apps",
                "--disable", "plugins",
                "-m", "gpt-6.1-sol", "-",
            ],
            call.Arguments
        );
        Assert.DoesNotContain("hooks", call.Arguments);
        Assert.DoesNotContain("fast_mode", call.Arguments);
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
        var runner = Codex(_ => new ProcessResult(0, s_fixture, ""));

        var result = await Provider(runner).CompleteAsync(s_request with { Model = null }, null, CancellationToken.None);

        Assert.DoesNotContain("-m", runner.Calls[^1].Arguments);
        Assert.DoesNotContain("mcp_servers={}", runner.Calls[^1].Arguments);
        Assert.Equal("default", result.Model);
    }

    [Fact]
    public async Task Stream_YieldsTheWholeReplyOnce()
    {
        var provider = Provider(Codex(_ => new ProcessResult(0, s_fixture, "")));
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
        var runner = Codex(_ => new ProcessResult(1, failed, ""));

        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            Provider(runner).CompleteAsync(s_request, null, CancellationToken.None)
        );

        Assert.Equal(ProviderFailureKind.ProcessFailed, ex.Kind);
        Assert.Equal("Codex exited with code 1. Codex does not accept the model 'gpt-6.1-sol'.", ex.Message);
    }

    [Fact]
    public async Task Complete_NoAgentMessageFailsEvenOnExitZero()
    {
        var runner = Codex(_ => new ProcessResult(0, "{\"type\":\"turn.started\"}\n", ""));

        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            Provider(runner).CompleteAsync(s_request, null, CancellationToken.None)
        );

        Assert.Equal(ProviderFailureKind.ProcessFailed, ex.Kind);
        Assert.Equal("Codex finished without a reply.", ex.Message);
    }

    [Fact]
    public async Task Complete_NeverEchoesDiagnosticsThatCouldQuoteThePrompt()
    {
        var runner = Codex(_ => new ProcessResult(2, "", "Invalid input: <draft>hi dana</draft>\nmore"));

        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            Provider(runner).CompleteAsync(s_request, null, CancellationToken.None)
        );

        Assert.Equal("Codex exited with code 2. Codex reported an error; run it from a terminal for details.", ex.Message);
        Assert.DoesNotContain("dana", ex.Message);
    }

    [Fact]
    public async Task Complete_AnUnknownFeatureFlagMeansCodexIsTooOld()
    {
        var runner = Codex(_ => new ProcessResult(1, "", "Error: Unknown feature flag: in_app_browser"));

        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            Provider(runner).CompleteAsync(s_request, null, CancellationToken.None)
        );

        Assert.Equal(ProviderFailureKind.Incompatible, ex.Kind);
        Assert.Equal("The Codex CLI is too old for Juvin. Update it and check again.", ex.Message);
    }

    [Fact]
    public async Task Complete_ClassifiesSignInAndUsageLimitErrors()
    {
        var signedOut = Codex(_ => new ProcessResult(1, "", "Error: not logged in, run codex login"));
        var limited = Codex(_ => new ProcessResult(1, "", "Error: 429 usage limit reached"));

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
        var runner = Codex(args =>
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
    public async Task Probe_ReportsACodexThatCannotDisableToolsAsIncompatible()
    {
        var runner = Codex(args =>
            args[0] switch
            {
                "--version" => new ProcessResult(0, "codex-cli 0.100.0\n", ""),
                "exec" => new ProcessResult(0, ExecHelp.Replace("  --disable <FEATURE>\n", ""), ""),
                _ => new ProcessResult(0, "", "Logged in using ChatGPT"),
            }
        );

        var status = await Provider(runner).ProbeAsync(CancellationToken.None);

        Assert.Equal(ProviderStatusKind.Incompatible, status.Kind);
    }

    [Fact]
    public async Task Complete_ATimeoutIsUnreachableNotCancelled()
    {
        var runner = Codex(_ => throw new OperationCanceledException());

        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            Provider(runner).CompleteAsync(s_request, null, CancellationToken.None)
        );

        Assert.Equal(ProviderFailureKind.Unreachable, ex.Kind);
        Assert.Equal("Codex did not answer within 1 minutes and was stopped.", ex.Message);
    }

    [Fact]
    public async Task Probe_WithoutABinaryIsNotInstalled()
    {
        var provider = new CodexCliProvider(new AppSettings(), new FakeProcessRunner(_ => throw new InvalidOperationException()), null, TimeSpan.FromMinutes(1));

        var status = await provider.ProbeAsync(CancellationToken.None);

        Assert.Equal(ProviderStatusKind.NotInstalled, status.Kind);
        Assert.Equal("Provider.Status.NotInstalled", status.MessageKey);
    }

    [Fact]
    public async Task Probe_WithAConfiguredPathThatIsMissing_SaysSoInsteadOfNotInstalled()
    {
        var settings = new AppSettings { CodexBinaryPath = "/nowhere/codex" };
        var provider = new CodexCliProvider(settings, new FakeProcessRunner(_ => throw new InvalidOperationException()), null, TimeSpan.FromMinutes(1));

        var status = await provider.ProbeAsync(CancellationToken.None);

        Assert.Equal(ProviderStatusKind.NotInstalled, status.Kind);
        Assert.Equal("Provider.Status.CodexPathMissing", status.MessageKey);
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

    [Fact]
    public void ResolveBinary_FindsTheCliInASearchDirectoryAfterPath()
    {
        var onPath = Directory.CreateDirectory(_temp.File("path")).FullName;
        var homebrew = Directory.CreateDirectory(_temp.File("homebrew")).FullName;
        File.WriteAllText(Path.Join(homebrew, "codex"), "");

        Assert.Equal(Path.Join(homebrew, "codex"), CodexCliProvider.ResolveBinary(null, SearchPath.Append(onPath, [homebrew]), isWindows: false));

        File.WriteAllText(Path.Join(onPath, "codex"), "");
        Assert.Equal(Path.Join(onPath, "codex"), CodexCliProvider.ResolveBinary(null, SearchPath.Append(onPath, [homebrew]), isWindows: false));
    }

    [Fact]
    public void ResolveBinary_SearchesPathThenTheSavedPathThenTheInstallDirectories()
    {
        var onPath = Directory.CreateDirectory(_temp.File("path")).FullName;
        var saved = Directory.CreateDirectory(_temp.File("saved")).FullName;
        var npm = Directory.CreateDirectory(_temp.File("npm")).FullName;
        File.WriteAllText(Path.Join(npm, "codex.cmd"), "");
        var search = SearchPath.Append(onPath, [saved, npm]);

        Assert.Equal(Path.Join(npm, "codex.cmd"), CodexCliProvider.ResolveBinary(null, search, isWindows: true));

        File.WriteAllText(Path.Join(saved, "codex.exe"), "");
        Assert.Equal(Path.Join(saved, "codex.exe"), CodexCliProvider.ResolveBinary(null, search, isWindows: true));

        File.WriteAllText(Path.Join(onPath, "codex.cmd"), "");
        Assert.Equal(Path.Join(onPath, "codex.cmd"), CodexCliProvider.ResolveBinary(null, search, isWindows: true));
    }

    [Fact]
    public void CreateDefault_ReadsTheSearchDirectoriesOnEachCodexSelection()
    {
        using var http = new HttpClient();
        var reads = 0;
        var selector = ProviderSelector.CreateDefault(
            http,
            new FakeProcessRunner(_ => throw new InvalidOperationException()),
            () =>
            {
                reads++;
                return [];
            }
        );
        var settings = new AppSettings { ProcessingMode = ProcessingMode.Codex };

        selector.Select(settings);
        selector.Select(settings);
        selector.Select(settings with { ProcessingMode = ProcessingMode.Local });

        Assert.Equal(2, reads);
    }

    [Fact]
    public void SearchPath_AppendsOnlyMissingFullyQualifiedDirectories()
    {
        var a = _temp.File("a");
        var b = _temp.File("b");
        var sep = Path.PathSeparator;

        Assert.Equal($"{a}{sep}{b}", SearchPath.Append(a, [a, "relative", b, b]));
        Assert.Equal(b, SearchPath.Append(null, [b]));
        Assert.Equal($"{sep}{a}", SearchPath.Append($"{sep}{a}", [a]));
        Assert.Equal("", SearchPath.Append("", []));
    }

    [Fact]
    public void ChildPath_AddsTheProgramsOwnDirectoryBeforeTheExtraOnes()
    {
        var nvm = _temp.File("nvm-bin");
        var extra = _temp.File("extra");
        var sep = Path.PathSeparator;
        var inherited = string.Join(sep, "/usr/bin", "/bin");

        Assert.Equal($"{inherited}{sep}{nvm}{sep}{extra}", ProcessRunner.ChildPath(inherited, Path.Join(nvm, "codex"), [extra]));
        Assert.Equal($"{inherited}{sep}{extra}", ProcessRunner.ChildPath(inherited, "open", [extra]));
        Assert.Equal(inherited, ProcessRunner.ChildPath(inherited, "open", []));
    }

    [Fact]
    public void ResolveBinary_IgnoresRelativePathEntries()
    {
        var absolute = Directory.CreateDirectory(_temp.File("abs")).FullName;
        var relative = Directory.CreateDirectory(_temp.File("rel")).FullName;
        File.WriteAllText(Path.Join(absolute, "codex"), "");
        File.WriteAllText(Path.Join(relative, "codex"), "");
        var current = Environment.CurrentDirectory;
        var fromCurrent = Path.GetRelativePath(current, relative);
        File.WriteAllText(Path.Join(current, "codex"), "");
        try
        {
            Assert.Null(CodexCliProvider.ResolveBinary(null, ".", isWindows: false));
            Assert.Null(CodexCliProvider.ResolveBinary(null, fromCurrent, isWindows: false));
            Assert.Equal(
                Path.Join(absolute, "codex"),
                CodexCliProvider.ResolveBinary(null, string.Join(Path.PathSeparator, ".", fromCurrent, absolute), isWindows: false)
            );
        }
        finally
        {
            File.Delete(Path.Join(current, "codex"));
        }
    }

    [Theory]
    [InlineData(1, FeaturesList)]
    [InlineData(0, "")]
    [InlineData(0, "unified_exec  stable  true\nbrowser_use  stable  true\n")]
    public async Task AFeatureListThatFailsOrLacksTheShellTool_MeansCodexIsTooOld(int exitCode, string features)
    {
        var runner = Codex(
            args =>
                args[0] switch
                {
                    "--version" => new ProcessResult(0, "codex-cli 0.120.0\n", ""),
                    "exec" when args[1] == "--help" => new ProcessResult(0, ExecHelp, ""),
                    "exec" => new ProcessResult(0, s_fixture, ""),
                    _ => new ProcessResult(0, "", "Logged in using ChatGPT"),
                },
            new ProcessResult(exitCode, features, "")
        );

        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            Provider(runner).CompleteAsync(s_request, null, CancellationToken.None)
        );
        var status = await Provider(runner).ProbeAsync(CancellationToken.None);

        Assert.Equal(ProviderFailureKind.Incompatible, ex.Kind);
        Assert.Equal(ProviderStatusKind.Incompatible, status.Kind);
        Assert.Equal("Provider.Status.CodexNeedsUpdate", status.MessageKey);
        Assert.DoesNotContain(runner.Calls, call => call.Arguments is ["exec", "--json", ..]);
    }

    [Fact]
    public async Task Complete_ListsTheFeaturesAndMcpServersOncePerInstance()
    {
        var runner = Codex(_ => new ProcessResult(0, s_fixture, ""));
        var provider = Provider(runner);

        await provider.CompleteAsync(s_request, null, CancellationToken.None);
        await provider.CompleteAsync(s_request, null, CancellationToken.None);

        Assert.Single(runner.Calls, call => call.Arguments[0] == "features");
        Assert.Single(runner.Calls, call => call.Arguments is ["mcp", "list", "--json"]);
        Assert.Equal(2, runner.Calls.Count(call => call.Arguments[0] == "exec"));
    }

    [Fact]
    public async Task Complete_WithNoMcpServersPassesNoConfigOverrides()
    {
        var runner = Codex(_ => new ProcessResult(0, s_fixture, ""), mcp: new ProcessResult(0, "[]", ""));

        await Provider(runner).CompleteAsync(s_request, null, CancellationToken.None);

        Assert.DoesNotContain("-c", runner.Calls[^1].Arguments);
    }

    [Theory]
    [InlineData(1, McpList)]
    [InlineData(0, "not json")]
    [InlineData(0, "{}")]
    public async Task AnMcpListThatFails_MeansCodexIsTooOld(int exitCode, string mcp)
    {
        var runner = Codex(
            args =>
                args[0] switch
                {
                    "--version" => new ProcessResult(0, "codex-cli 0.120.0\n", ""),
                    "exec" when args[1] == "--help" => new ProcessResult(0, ExecHelp, ""),
                    "exec" => new ProcessResult(0, s_fixture, ""),
                    _ => new ProcessResult(0, "", "Logged in using ChatGPT"),
                },
            mcp: new ProcessResult(exitCode, mcp, "")
        );

        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            Provider(runner).CompleteAsync(s_request, null, CancellationToken.None)
        );
        var status = await Provider(runner).ProbeAsync(CancellationToken.None);

        Assert.Equal(ProviderFailureKind.Incompatible, ex.Kind);
        Assert.Equal(ProviderStatusKind.Incompatible, status.Kind);
        Assert.Equal("Provider.Status.CodexNeedsUpdate", status.MessageKey);
    }

    [Theory]
    [InlineData("my server")]
    [InlineData("a.b")]
    [InlineData("")]
    public async Task Complete_AnMcpServerNameThatCannotBeDisabledIsAConfigurationFailure(string name)
    {
        var runner = Codex(
            args =>
                args[0] switch
                {
                    "--version" => new ProcessResult(0, "codex-cli 0.163.0\n", ""),
                    "exec" when args[1] == "--help" => new ProcessResult(0, ExecHelp, ""),
                    "exec" => new ProcessResult(0, s_fixture, ""),
                    _ => new ProcessResult(0, "", "Logged in using ChatGPT"),
                },
            mcp: new ProcessResult(0, $$"""[{"name":"docs"},{"name":"{{name}}"}]""", "")
        );

        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            Provider(runner).CompleteAsync(s_request, null, CancellationToken.None)
        );
        var status = await Provider(runner).ProbeAsync(CancellationToken.None);

        Assert.Equal(ProviderStatusKind.Incompatible, status.Kind);
        Assert.Equal("Provider.Status.CodexMcpServerName", status.MessageKey);
        Assert.Equal(ProviderFailureKind.Configuration, ex.Kind);
        Assert.Equal("An MCP server in the Codex configuration has a name Juvin cannot disable. Rename it or remove it.", ex.Message);
        Assert.DoesNotContain(runner.Calls, call => call.Arguments is ["exec", "--json", ..]);
    }

    /// <summary>Answers <c>codex features list</c> and <c>codex mcp list</c> with canned output unless given other results.</summary>
    private static FakeProcessRunner Codex(
        Func<IReadOnlyList<string>, ProcessResult> respond,
        ProcessResult? features = null,
        ProcessResult? mcp = null
    ) =>
        new(args =>
            args[0] switch
            {
                "features" => features ?? new ProcessResult(0, FeaturesList, ""),
                "mcp" => mcp ?? new ProcessResult(0, McpList, ""),
                _ => respond(args),
            }
        );

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
    public async Task ArgumentsTheShellCannotTake_AreAConfigurationFailureAndACannotStartProbe()
    {
        var runner = new FakeProcessRunner(_ => throw new ArgumentException("bad"));
        var provider = Provider(runner);

        var ex = await Assert.ThrowsAsync<ProviderException>(() => provider.CompleteAsync(s_request, null, CancellationToken.None));

        Assert.Equal(ProviderFailureKind.Configuration, ex.Kind);
        var status = await provider.ProbeAsync(CancellationToken.None);
        Assert.Equal(ProviderStatusKind.NotInstalled, status.Kind);
        Assert.Equal("Provider.Status.CodexCannotStart", status.MessageKey);
    }

    [Fact]
    public void BuildStartInfo_QuotesAnEmptyArgument()
    {
        var info = ProcessRunner.BuildStartInfo(@"C:\npm\codex.cmd", ["-m", "", "-"], isWindows: true);

        Assert.Contains("""-m "" -""", info.Arguments);
    }
}
