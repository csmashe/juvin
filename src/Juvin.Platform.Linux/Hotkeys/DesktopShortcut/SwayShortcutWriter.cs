using Juvin.Core.Providers;

namespace Juvin.Platform.Linux.Hotkeys.DesktopShortcut;

/// <summary>Keeps a <c>bindsym</c> in a marked block of <c>~/.config/sway/config</c> and applies it with <c>swaymsg reload</c>.</summary>
public sealed class SwayShortcutWriter : IDesktopShortcutWriter
{
    private readonly IProcessRunner _processRunner;
    private readonly DesktopSession _session;

    public SwayShortcutWriter(IProcessRunner processRunner)
        : this(processRunner, DesktopSession.FromEnvironment())
    {
    }

    internal SwayShortcutWriter(IProcessRunner processRunner, DesktopSession session)
    {
        _processRunner = processRunner;
        _session = session;
    }

    public string DesktopId => "sway";

    public bool RequiresSessionRestartToApply => false;

    private string Config => Path.Join(_session.ConfigHome, "sway", "config");

    private string ReloadMarker => Path.Join(_session.ConfigHome, "sway", ".juvin-reload-pending");

    public bool IsCurrentDesktop() => _session.DesktopId == DesktopId && _session.HasExecutable("swaymsg");

    public Task<bool> IsInstalledAsync(DesktopShortcutSpec spec, CancellationToken ct)
    {
        var block = SentinelBlock.Read(SentinelBlock.ReadFile(Config) ?? "");
        return Task.FromResult(block is not null && block.SequenceEqual(Lines(spec)) && !File.Exists(ReloadMarker));
    }

    public async Task WriteAsync(DesktopShortcutSpec spec, CancellationToken ct)
    {
        // Without a user config Sway reads the system one; a new file holding only Juvin's bind would replace it.
        var existing = SentinelBlock.ReadFile(Config)
            ?? throw new InvalidOperationException($"{Config} does not exist. Copy the system Sway config there first, then try again.");
        if (SentinelBlock.WriteIfChanged(Config, existing, SentinelBlock.Upsert(existing, Lines(spec))) || File.Exists(ReloadMarker))
        {
            await ReloadAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task RemoveAsync(string id, CancellationToken ct)
    {
        var existing = SentinelBlock.ReadFile(Config);
        if ((existing is not null && SentinelBlock.WriteIfChanged(Config, existing, SentinelBlock.Remove(existing))) || File.Exists(ReloadMarker))
        {
            await ReloadAsync(ct).ConfigureAwait(false);
        }
    }

    private static List<string> Lines(DesktopShortcutSpec spec) =>
        [$"bindsym --no-repeat {DesktopTriggers.Sway(spec.Chord)} exec {spec.Command}"];
    private Task ReloadAsync(CancellationToken ct) => DesktopSession.ReloadAsync(_processRunner, "swaymsg", ReloadMarker, ct);
}
