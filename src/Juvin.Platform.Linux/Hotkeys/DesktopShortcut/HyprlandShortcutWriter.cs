using Juvin.Core.Providers;

namespace Juvin.Platform.Linux.Hotkeys.DesktopShortcut;

/// <summary>
///     Keeps the bind in its own <c>~/.config/hypr/juvin.conf</c>, sourced once from
///     <c>hyprland.conf</c>, and applies it live with <c>hyprctl reload</c>.
/// </summary>
public sealed class HyprlandShortcutWriter : IDesktopShortcutWriter
{
    private readonly IProcessRunner _processRunner;
    private readonly DesktopSession _session;

    public HyprlandShortcutWriter(IProcessRunner processRunner)
        : this(processRunner, DesktopSession.FromEnvironment())
    {
    }

    internal HyprlandShortcutWriter(IProcessRunner processRunner, DesktopSession session)
    {
        _processRunner = processRunner;
        _session = session;
    }

    public string DesktopId => "hyprland";

    public bool RequiresSessionRestartToApply => false;

    private string MainConfig => Path.Join(_session.ConfigHome, "hypr", "hyprland.conf");

    private string JuvinConfig => Path.Join(_session.ConfigHome, "hypr", "juvin.conf");

    private string SourceLine => $"source = {JuvinConfig}";

    private string ReloadMarker => Path.Join(_session.ConfigHome, "hypr", "juvin.conf.reload-pending");

    public bool IsCurrentDesktop() => _session.DesktopId == DesktopId && _session.HasExecutable("hyprctl");

    public Task<bool> IsInstalledAsync(DesktopShortcutSpec spec, CancellationToken ct)
    {
        var block = SentinelBlock.Read(SentinelBlock.ReadFile(JuvinConfig) ?? "");
        return Task.FromResult(
            block is not null && block.SequenceEqual(Lines(spec)) && IsSourced(SentinelBlock.ReadFile(MainConfig)) && !File.Exists(ReloadMarker)
        );
    }

    public async Task WriteAsync(DesktopShortcutSpec spec, CancellationToken ct)
    {
        // Hyprland writes a default config on first start; creating one here would replace it.
        var main = SentinelBlock.ReadFile(MainConfig)
            ?? throw new InvalidOperationException($"{MainConfig} does not exist. Start Hyprland once so it creates its config, then try again.");
        var existing = SentinelBlock.ReadFile(JuvinConfig);
        var changed = SentinelBlock.WriteIfChanged(JuvinConfig, existing, SentinelBlock.Upsert(existing ?? "", Lines(spec)));
        if (!IsSourced(main))
        {
            SentinelBlock.WriteIfChanged(MainConfig, main, SentinelBlock.AppendLine(main, SourceLine));
            changed = true;
        }

        if (changed || File.Exists(ReloadMarker))
        {
            await ReloadAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Empties the block but keeps <c>juvin.conf</c>, because Hyprland reports an error for a missing sourced file.</summary>
    public async Task RemoveAsync(string id, CancellationToken ct)
    {
        var existing = SentinelBlock.ReadFile(JuvinConfig);
        if ((existing is not null && SentinelBlock.WriteIfChanged(JuvinConfig, existing, SentinelBlock.Remove(existing))) || File.Exists(ReloadMarker))
        {
            await ReloadAsync(ct).ConfigureAwait(false);
        }
    }

    private bool IsSourced(string? main) =>
        main is not null && main.Split('\n').Any(SourcesJuvinConfig);

    /// <summary>A <c>source</c> line naming <c>juvin.conf</c>, or a <c>*</c> or <c>*.conf</c> glob in its directory, after <c>~</c> and <c>$HOME</c> expand.</summary>
    private bool SourcesJuvinConfig(string line)
    {
        var parts = line.Split('=', 2, StringSplitOptions.TrimEntries);
        if (parts is not ["source", var value])
        {
            return false;
        }

        // ReSharper disable once InvertIf -- the expansion reads as one guarded block
        if (_session.GetEnvironment("HOME") is { Length: > 0 } home)
        {
            if (value.StartsWith("~/", StringComparison.Ordinal))
            {
                value = Path.Join(home, value[2..]);
            }
            else if (value.StartsWith("$HOME/", StringComparison.Ordinal))
            {
                value = Path.Join(home, value[6..]);
            }
        }

        return value == JuvinConfig
            || (Path.GetFileName(value) is "*" or "*.conf" && Path.GetDirectoryName(value) == Path.GetDirectoryName(JuvinConfig));
    }

    private static List<string> Lines(DesktopShortcutSpec spec)
    {
        var (modifiers, key) = DesktopTriggers.Hyprland(spec.Chord);
        return [$"bind = {modifiers}, {key}, exec, {spec.Command}"];
    }
    private Task ReloadAsync(CancellationToken ct) => DesktopSession.ReloadAsync(_processRunner, "hyprctl", ReloadMarker, ct);
}
