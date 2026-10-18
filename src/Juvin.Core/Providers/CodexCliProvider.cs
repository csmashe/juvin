using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Juvin.Core.Models;

namespace Juvin.Core.Providers;

/// <summary>
///     Codex mode: runs the signed-in Codex CLI as a child process with a read-only sandbox and no
///     persisted session. Codex returns the reply whole, so this provider does not stream.
/// </summary>
public sealed class CodexCliProvider : IModelProvider
{
    private static readonly string[] s_requiredExecFlags = ["--json", "--ephemeral", "--sandbox", "--skip-git-repo-check", "--disable"];
    // The read-only sandbox still lets a shell or exec tool read any file, and the thread can carry
    // an injected instruction to quote one; the others reach the network, the screen, or more agents.
    private static readonly string[] s_disabledFeatures =
    [
        "shell_tool",
        "unified_exec",
        "browser_use",
        "computer_use",
        "in_app_browser",
        "view_image",
        "multi_agent",
        "apps",
        "plugins",
        "hooks",
    ];

    private static readonly TimeSpan s_defaultTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan s_probeTimeout = TimeSpan.FromSeconds(30);

    private readonly string? _binary;
    private readonly string? _model;
    private readonly IProcessRunner _runner;
    private readonly TimeSpan _timeout;

    private IReadOnlySet<string>? _features;
    private IReadOnlyList<string>? _mcpServers;

    /// <param name="settings">The configured binary and model.</param>
    /// <param name="runner">Runs the CLI.</param>
    /// <param name="searchDirectories">Searched after PATH, which lacks the usual install locations when the desktop started the app.</param>
    public CodexCliProvider(AppSettings settings, IProcessRunner runner, IReadOnlyList<string> searchDirectories)
        : this(
            settings,
            runner,
            ResolveBinary(
                settings.CodexBinaryPath,
                SearchPath.Append(Environment.GetEnvironmentVariable("PATH"), searchDirectories),
                OperatingSystem.IsWindows()
            ),
            s_defaultTimeout
        )
    {
    }

    internal CodexCliProvider(
        AppSettings settings,
        IProcessRunner runner,
        string? binary,
        TimeSpan timeout
    )
    {
        _runner = runner;
        _binary = binary;
        _timeout = timeout;
        _model = string.IsNullOrWhiteSpace(settings.CodexModel) ? null : settings.CodexModel.Trim();
    }

    public ProcessingMode Mode => ProcessingMode.Codex;

    public string DisplayName => "Codex";

    public bool SupportsStreaming => false;

    public bool RunsLocally => false;

    public async Task<ProviderStatus> ProbeAsync(CancellationToken ct)
    {
        if (_binary is null)
        {
            return new ProviderStatus(ProviderStatusKind.NotInstalled, "Provider.Status.NotInstalled", []);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(s_probeTimeout);
        try
        {
            var version = await _runner.RunAsync(_binary, ["--version"], null, null, timeout.Token);
            var versionText = version.ExitCode == 0 ? FirstLine(version.StandardOutput) : null;
            var models = _model is null ? [] : new[] { new ModelInfo(_model, _model, null, false) };

            // An older Codex passes the version and sign-in checks but lacks the flags every rewrite uses.
            var help = await _runner.RunAsync(_binary, ["exec", "--help"], null, null, timeout.Token);
            if (
                help.ExitCode != 0
                || s_requiredExecFlags.Any(flag => !help.StandardOutput.Contains(flag, StringComparison.Ordinal))
                || await FeaturesAsync(_binary, timeout.Token) is null
                || await McpServersAsync(_binary, timeout.Token) is not { } mcpServers
            )
            {
                return new ProviderStatus(ProviderStatusKind.Incompatible, "Provider.Status.CodexNeedsUpdate", models, versionText);
            }

            if (!mcpServers.All(CanDisable))
            {
                return new ProviderStatus(ProviderStatusKind.Incompatible, "Provider.Status.CodexMcpServerName", models, versionText);
            }

            var login = await _runner.RunAsync(_binary, ["login", "status"], null, null, timeout.Token);

            // Codex 0.162 prints "Logged in using ChatGPT" to stderr; accept either stream.
            var signedIn =
                login.ExitCode == 0
                && (login.StandardOutput + login.StandardError).Contains("Logged in", StringComparison.Ordinal);
            return signedIn
                ? new ProviderStatus(ProviderStatusKind.Ready, "Provider.Status.Ready", models, versionText)
                : new ProviderStatus(ProviderStatusKind.NotSignedIn, "Provider.Status.NotSignedIn", models, versionText);
        }
        catch (Exception ex) when (ex is Win32Exception or ArgumentException)
        {
            return new ProviderStatus(ProviderStatusKind.NotInstalled, "Provider.Status.NotInstalled", []);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ProviderStatus(ProviderStatusKind.Unreachable, "Provider.Status.CodexNotResponding", []);
        }
    }

    public async IAsyncEnumerable<string> StreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        var result = await CompleteAsync(request, null, ct);
        yield return result.Text;
    }

    public async Task<ChatResult> CompleteAsync(
        ChatRequest request,
        IProgress<string>? preview,
        CancellationToken ct
    )
    {
        var binary =
            _binary
            ?? throw new ProviderException(
                ProviderFailureKind.Configuration,
                "The Codex CLI was not found. Install it or set its path in Models."
            );
        var model = request.Model ?? _model;
        var arguments = await ExecArgumentsAsync(binary, model, ct);

        var prompt = FlattenPrompt(request);
        var stopwatch = Stopwatch.StartNew();

        // An empty working directory keeps Codex from reading project files or AGENTS.md.
        DirectoryInfo workingDirectory;
        try
        {
            workingDirectory = Directory.CreateTempSubdirectory("juvin-codex-");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ProviderException(
                ProviderFailureKind.Configuration,
                "Juvin could not create a temporary folder for Codex.",
                ex
            );
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeout);
        ProcessResult result;
        try
        {
            result = await _runner.RunAsync(binary, arguments, prompt, workingDirectory.FullName, timeout.Token);
        }
        catch (Win32Exception ex)
        {
            throw new ProviderException(ProviderFailureKind.Configuration, "The Codex CLI could not be started.", ex);
        }
        catch (ArgumentException ex)
        {
            throw new ProviderException(
                ProviderFailureKind.Configuration,
                "The Codex path or model name has characters that cannot be passed to the Codex CLI on Windows.",
                ex
            );
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ProviderException(
                ProviderFailureKind.Unreachable,
                $"Codex did not answer within {_timeout.TotalMinutes:0} minutes and was stopped.",
                ex
            );
        }
        finally
        {
            TryDelete(workingDirectory);
        }

        var output = ParseEvents(result.StandardOutput);
        if (result.ExitCode != 0 || output.Text is null)
        {
            Trace.WriteLine(
                $"[CodexCliProvider] Failed: exit={result.ExitCode}, message={output.Text is not null}, stderrChars={result.StandardError.Length}"
            );
            // A backstop: only features Codex listed are disabled, so it should know every name.
            if ((output.Error + result.StandardError).Contains("Unknown feature flag", StringComparison.OrdinalIgnoreCase))
            {
                throw TooOld();
            }

            throw new ProviderException(
                ProviderFailureKind.ProcessFailed,
                FailureMessage(result, output.Error, model)
            );
        }

        var text = output.Text.TrimEnd();
        Trace.WriteLine(
            $"[CodexCliProvider] Completed: model={model ?? IModelProvider.DefaultModelLabel}, chars={text.Length}, ms={stopwatch.ElapsedMilliseconds}"
        );
        preview?.Report(text);
        return new ChatResult(text, model ?? IModelProvider.DefaultModelLabel, output.InputTokens, output.OutputTokens);
    }

    private async Task<List<string>> ExecArgumentsAsync(string binary, string? model, CancellationToken ct)
    {
        IReadOnlySet<string>? features;
        IReadOnlyList<string>? mcpServers;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(s_probeTimeout);
            try
            {
                features = await FeaturesAsync(binary, timeout.Token);
                mcpServers = features is null ? null : await McpServersAsync(binary, timeout.Token);
            }
            catch (Exception ex) when (ex is Win32Exception or ArgumentException)
            {
                throw new ProviderException(ProviderFailureKind.Configuration, "The Codex CLI could not be started.", ex);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new ProviderException(ProviderFailureKind.Unreachable, "Codex did not respond and was stopped.", ex);
            }
        }

        if (features is null || mcpServers is null)
        {
            throw TooOld();
        }

        List<string> arguments =
        [
            "exec",
            "--json",
            "--ephemeral",
            "--sandbox",
            "read-only",
            "--skip-git-repo-check",
        ];
        // Codex merges config tables, so an empty mcp_servers table would not remove the user's
        // servers; each is switched off by name.
        foreach (var server in mcpServers)
        {
            if (!CanDisable(server))
            {
                Trace.WriteLine($"[CodexCliProvider] MCP server name not disableable: chars={server.Length}");
                throw new ProviderException(
                    ProviderFailureKind.Configuration,
                    "An MCP server in the Codex configuration has a name Juvin cannot disable. Rename it or remove it."
                );
            }

            arguments.AddRange(["-c", $"mcp_servers.{server}.enabled=false"]);
        }

        // Codex refuses a feature name it does not know, and a feature it lacks is a tool it cannot offer.
        foreach (var feature in s_disabledFeatures.Where(features.Contains))
        {
            arguments.AddRange(["--disable", feature]);
        }

        if (model is not null)
        {
            arguments.AddRange(["-m", model]);
        }

        arguments.Add("-");
        return arguments;
    }

    /// <summary>
    ///     The feature names the installed Codex reports, cached per instance; null when it cannot
    ///     list them or has no shell tool to turn off, which means it is too old to run safely.
    /// </summary>
    private async Task<IReadOnlySet<string>?> FeaturesAsync(string binary, CancellationToken ct)
    {
        if (_features is not null)
        {
            return _features;
        }

        var result = await _runner.RunAsync(binary, ["features", "list"], null, null, ct);
        if (result.ExitCode != 0)
        {
            return null;
        }

        var names = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)[0])
            .ToHashSet(StringComparer.Ordinal);
        if (!names.Contains("shell_tool"))
        {
            return null;
        }

        _features = names;
        return names;
    }

    /// <summary>
    ///     The names of every MCP server the Codex configuration lists, enabled or not, cached per
    ///     instance; null when Codex cannot list them.
    /// </summary>
    private async Task<IReadOnlyList<string>?> McpServersAsync(string binary, CancellationToken ct)
    {
        if (_mcpServers is not null)
        {
            return _mcpServers;
        }

        var result = await _runner.RunAsync(binary, ["mcp", "list", "--json"], null, null, ct);
        if (result.ExitCode != 0)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            List<string> names = [];
            foreach (var server in document.RootElement.EnumerateArray())
            {
                if (GetString(server, "name") is not { } name)
                {
                    return null;
                }

                names.Add(name);
            }

            _mcpServers = names;
            return names;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The name lands in a TOML key path, so only a bare key is passed.</summary>
    private static bool CanDisable(string server) =>
        server.Length > 0 && server.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    private static ProviderException TooOld() =>
        new(ProviderFailureKind.Incompatible, "The Codex CLI is too old for Juvin. Update it and check again.");

    internal static string? ResolveBinary(string? configured, string? pathVariable, bool isWindows)
    {
        // Rewrites run in a temporary directory, so a relative path has to be pinned down here.
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return File.Exists(configured) ? Path.GetFullPath(configured) : null;
        }

        // On Windows the bare "codex" npm shim is a shell script that cannot be started directly.
        string[] names = isWindows ? ["codex.exe", "codex.cmd"] : ["codex"];
        // A relative entry such as "." (or "C:bin" on Windows) resolves against the current directory.
        var directories = (pathVariable ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directory => directory.Trim('"'))
            .Where(Path.IsPathFullyQualified);
        var found = directories
            .SelectMany(directory => names.Select(name => Path.Join(directory, name)))
            .FirstOrDefault(File.Exists);
        return found is null ? null : Path.GetFullPath(found);
    }

    private static string FlattenPrompt(ChatRequest request)
    {
        var prompt = new StringBuilder();
        prompt.Append("System instructions:\n").Append(request.SystemPrompt).Append("\n\n");
        foreach (var message in request.Messages)
        {
            prompt
                .Append(message.Role == ChatRole.User ? "User:\n" : "Assistant:\n")
                .Append(message.Content)
                .Append("\n\n");
        }

        prompt.Append("Reply to the last User message, as the assistant.");
        return prompt.ToString();
    }

    internal static CodexOutput ParseEvents(string jsonl)
    {
        var output = new CodexOutput();
        foreach (var line in jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                output = GetString(root, "type") switch
                {
                    "item.completed"
                        when root.TryGetProperty("item", out var item)
                            && GetString(item, "type") == "agent_message" =>
                        output with { Text = GetString(item, "text") ?? output.Text },
                    "turn.completed" when root.TryGetProperty("usage", out var usage) =>
                        output with
                        {
                            InputTokens = GetInt(usage, "input_tokens"),
                            OutputTokens = GetInt(usage, "output_tokens"),
                        },
                    "turn.failed" when root.TryGetProperty("error", out var error) =>
                        output with { Error = ErrorDetail(GetString(error, "message")) ?? output.Error },
                    "error" => output with { Error = output.Error ?? ErrorDetail(GetString(root, "message")) },
                    _ => output,
                };
            }
        }

        return output;
    }

    /// <summary>Codex wraps API errors as a JSON string; the inner message is the readable part.</summary>
    private static string? ErrorDetail(string? message)
    {
        if (message is null || !message.TrimStart().StartsWith('{'))
        {
            return message;
        }

        try
        {
            using var document = JsonDocument.Parse(message);
            return document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                    ? GetString(error, "message") ?? message
                    : message;
        }
        catch (JsonException)
        {
            return message;
        }
    }

    /// <summary>
    ///     Codex diagnostics are classified, never echoed: an API error can quote the prompt, and the
    ///     prompt holds the draft and thread.
    /// </summary>
    private static string FailureMessage(ProcessResult result, string? error, string? model)
    {
        var message = result.ExitCode != 0
            ? $"Codex exited with code {result.ExitCode}."
            : "Codex finished without a reply.";
        var detail = error ?? FirstLine(result.StandardError) ?? "";
        var reason = detail.ToLowerInvariant() switch
        {
            var d when d.Contains("model") && (d.Contains("not supported") || d.Contains("not found") || d.Contains("does not exist") || d.Contains("invalid")) =>
                $"Codex does not accept the model '{model ?? IModelProvider.DefaultModelLabel}'.",
            var d when d.Contains("unauthorized") || d.Contains("401") || d.Contains("login") || d.Contains("not logged in") =>
                "Codex is not signed in. Run codex login.",
            var d when d.Contains("rate limit") || d.Contains("429") || d.Contains("quota") || d.Contains("usage limit") =>
                "Codex reached its usage limit.",
            "" => null,
            _ => "Codex reported an error; run it from a terminal for details.",
        };
        return reason is null ? message : message + " " + reason;
    }

    private static string? FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : null;

    private static void TryDelete(DirectoryInfo directory)
    {
        try
        {
            directory.Delete(recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort; the directory is empty unless Codex wrote into it.
        }
    }

    internal sealed record CodexOutput(
        string? Text = null,
        int? InputTokens = null,
        int? OutputTokens = null,
        string? Error = null
    );
}
