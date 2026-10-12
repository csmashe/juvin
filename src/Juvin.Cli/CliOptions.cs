using Juvin.Core.Models;
using Juvin.Core.Rewrite;

namespace Juvin.Cli;

internal sealed record CliOptions
{
    public bool Help { get; private init; }

    /// <summary>The --action value; null runs Clearer, or the free-text action when --instruction is given.</summary>
    private string? ActionId { get; init; }

    public string? Instruction { get; private init; }

    public ProcessingMode? Mode { get; private init; }

    public bool Provenance { get; private init; }

    public string? SettingsPath { get; private init; }

    private string? Model { get; init; }

    private string? Server { get; init; }

    /// <summary>Applies the per-run overrides; nothing is saved.</summary>
    public AppSettings ApplyTo(AppSettings settings)
    {
        var mode = Mode ?? settings.ProcessingMode;
        var result = settings with
        {
            ProcessingMode = mode,
            LocalServerUrl = Server ?? settings.LocalServerUrl,
            ProvenanceCaptureEnabled = settings.ProvenanceCaptureEnabled || Provenance,
        };
        if (Model is null)
        {
            return result;
        }

        return mode == ProcessingMode.Local
            ? result with { LocalModel = Model }
            : result with { CodexModel = Model };
    }

    /// <returns>The options, or null with <paramref name="error" /> set for a usage error.</returns>
    public static CliOptions? Parse(IReadOnlyList<string> args, out string? error)
    {
        error = null;
        if (args.Count == 0)
        {
            error = "No command given.";
            return null;
        }

        if (args[0] is "--help" or "-h" or "help")
        {
            return new CliOptions { Help = true };
        }

        if (args[0] != "rewrite")
        {
            error = $"Unknown command '{args[0]}'.";
            return null;
        }

        var options = new CliOptions();
        var stdin = false;
        string? actionId = null;
        for (var i = 1; i < args.Count; i++)
        {
            var name = args[i];
            switch (name)
            {
                case "--help" or "-h":
                    return new CliOptions { Help = true };
                case "--stdin":
                    stdin = true;
                    continue;
                case "--provenance":
                    options = options with { Provenance = true };
                    continue;
            }

            if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                error = name.StartsWith("--", StringComparison.Ordinal)
                    ? $"{name} needs a value."
                    : $"Unexpected argument '{name}'.";
                return null;
            }

            var value = args[++i];
            switch (name)
            {
                case "--action":
                    actionId = value;
                    break;
                case "--instruction":
                    options = options with { Instruction = value };
                    break;
                case "--mode" when Enum.TryParse<ProcessingMode>(value, ignoreCase: true, out var mode) && Enum.IsDefined(mode):
                    options = options with { Mode = mode };
                    break;
                case "--mode":
                    error = $"Unknown mode '{value}'. Use local or codex.";
                    return null;
                case "--model":
                    options = options with { Model = value };
                    break;
                case "--server":
                    options = options with { Server = value };
                    break;
                case "--settings":
                    options = options with { SettingsPath = value };
                    break;
                default:
                    error = $"Unknown option '{name}'.";
                    return null;
            }
        }

        if (!stdin)
        {
            error = "rewrite reads its input from stdin; pass --stdin.";
            return null;
        }

        var isFreeText = actionId is null
            ? options.Instruction is not null
            : string.Equals(actionId, RewriteAction.Custom.Id, StringComparison.OrdinalIgnoreCase);
        if (isFreeText != options.Instruction is not null)
        {
            error = "--instruction goes with the custom action, and the custom action needs --instruction.";
            return null;
        }

        // ReSharper disable once InvertIf -- a guard that reads top-down like the ones above it
        if (string.IsNullOrWhiteSpace(options.Instruction) && options.Instruction is not null)
        {
            error = "--instruction needs some text.";
            return null;
        }

        return options with { ActionId = actionId };
    }

    /// <returns>The action, or null with <paramref name="error" /> set when the id is unknown.</returns>
    public RewriteAction? ResolveAction(RewriteActionCatalog catalog, out string? error)
    {
        error = null;
        if (ActionId is null)
        {
            return Instruction is null ? catalog.Find(RewriteAction.Clearer.Id) : RewriteAction.Custom;
        }

        var action = catalog.Find(ActionId);
        if (action is null)
        {
            error = $"Unknown action '{ActionId}'. Use one of: {string.Join(", ", catalog.All.Select(a => a.Id))}.";
        }

        return action;
    }
}
