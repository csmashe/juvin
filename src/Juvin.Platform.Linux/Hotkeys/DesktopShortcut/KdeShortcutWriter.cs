using System.Text;

namespace Juvin.Platform.Linux.Hotkeys.DesktopShortcut;

/// <summary>
///     Writes a <c>Type=Service</c> entry (KGlobalAccel ignores <c>Type=Application</c>) to
///     <c>~/.local/share/kglobalaccel/</c>, read only at session start; the live D-Bus registration
///     differs across Plasma versions.
/// </summary>
public sealed class KdeShortcutWriter : IDesktopShortcutWriter
{
    private const string ManagedLine = "X-Juvin-Managed=true";

    private readonly DesktopSession _session;

    public KdeShortcutWriter()
        : this(DesktopSession.FromEnvironment())
    {
    }

    internal KdeShortcutWriter(DesktopSession session)
    {
        _session = session;
    }

    public string DesktopId => "kde";

    public bool RequiresSessionRestartToApply => true;

    public bool IsCurrentDesktop() => _session.DesktopId == DesktopId;

    public Task<bool> IsInstalledAsync(DesktopShortcutSpec spec, CancellationToken ct)
    {
        var path = EntryPath(spec.Id);
        return Task.FromResult(File.Exists(path) && File.ReadAllText(path) == Entry(spec));
    }

    public Task WriteAsync(DesktopShortcutSpec spec, CancellationToken ct)
    {
        var path = EntryPath(spec.Id);
        var existing = SentinelBlock.ReadFile(path);
        if (existing is not null && !IsManaged(existing))
        {
            throw new InvalidOperationException($"{path} exists and was not written by Juvin. Remove or rename it, then try again.");
        }

        SentinelBlock.WriteIfChanged(path, existing, Entry(spec));
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string id, CancellationToken ct)
    {
        var path = EntryPath(id);
        var existing = SentinelBlock.ReadFile(path);
        if (existing is not null && IsManaged(existing))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private string EntryPath(string id) => Path.Join(_session.DataHome, "kglobalaccel", id.Replace('.', '-') + ".desktop");

    private static string Entry(DesktopShortcutSpec spec) =>
        $"""
         [Desktop Entry]
         Type=Service
         Name={Escape(spec.DisplayName)}
         Exec={Escape(spec.Command).Replace("%", "%%")}
         X-KDE-Shortcuts={Escape(DesktopTriggers.Kde(spec.Chord))}
         X-KDE-StartupNotify=false
         {ManagedLine}

         """;

    private static bool IsManaged(string contents) =>
        contents.Split('\n').Any(line => line.TrimEnd() == ManagedLine);

    /// <summary>Desktop Entry string escaping, applied over the command's own shell quoting.</summary>
    private static string Escape(string value)
    {
        var escaped = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            escaped.Append(
                c switch
                {
                    '\\' => @"\\",
                    '\n' => @"\n",
                    '\r' => @"\r",
                    '\t' => @"\t",
                    _ => c.ToString(),
                }
            );
        }

        return escaped.ToString();
    }}
