using System.Diagnostics;
using System.Text;
using Juvin.Core.Providers;

namespace Juvin.Platform.Linux.Hotkeys.DesktopShortcut;

/// <summary>
///     Adds a GNOME custom shortcut through <c>gsettings</c>. The user's custom-keybindings list is
///     read, extended with Juvin's path, and written back whole; a list that cannot be parsed is
///     never written, so the user's other shortcuts cannot be lost. Applies live.
/// </summary>
public sealed class GnomeShortcutWriter : IDesktopShortcutWriter
{
    private const string Gsettings = "gsettings";
    private const string MediaKeysSchema = "org.gnome.settings-daemon.plugins.media-keys";
    private const string CustomKeybindingSchema = MediaKeysSchema + ".custom-keybinding";
    private const string ListKey = "custom-keybindings";

    private readonly IProcessRunner _processRunner;
    private readonly DesktopSession _session;

    public GnomeShortcutWriter(IProcessRunner processRunner)
        : this(processRunner, DesktopSession.FromEnvironment())
    {
    }

    internal GnomeShortcutWriter(IProcessRunner processRunner, DesktopSession session)
    {
        _processRunner = processRunner;
        _session = session;
    }

    public string DesktopId => "gnome";

    public bool RequiresSessionRestartToApply => false;

    public bool IsCurrentDesktop() => _session.DesktopId == DesktopId && _session.HasExecutable(Gsettings);

    public async Task<bool> IsInstalledAsync(DesktopShortcutSpec spec, CancellationToken ct)
    {
        var path = CustomPath(spec.Id);
        if (!ParseList(await GetAsync(MediaKeysSchema, ListKey, ct).ConfigureAwait(false)).Contains(path))
        {
            return false;
        }

        var schema = $"{CustomKeybindingSchema}:{path}";
        return ParseString(await GetAsync(schema, "command", ct).ConfigureAwait(false)) == spec.Command
            && ParseString(await GetAsync(schema, "binding", ct).ConfigureAwait(false)) == DesktopTriggers.Gnome(spec.Chord)
            && ParseString(await GetAsync(schema, "name", ct).ConfigureAwait(false)) == spec.DisplayName;
    }

    public async Task WriteAsync(DesktopShortcutSpec spec, CancellationToken ct)
    {
        var path = CustomPath(spec.Id);
        var list = ParseList(await GetAsync(MediaKeysSchema, ListKey, ct).ConfigureAwait(false));

        // The entry is complete before it is listed, so GNOME never shows a half-written shortcut.
        var schema = $"{CustomKeybindingSchema}:{path}";
        await SetAsync(schema, "name", FormatString(spec.DisplayName), ct).ConfigureAwait(false);
        await SetAsync(schema, "command", FormatString(spec.Command), ct).ConfigureAwait(false);
        await SetAsync(schema, "binding", FormatString(DesktopTriggers.Gnome(spec.Chord)), ct).ConfigureAwait(false);

        if (!list.Contains(path))
        {
            await SetAsync(MediaKeysSchema, ListKey, FormatList([.. list, path]), ct).ConfigureAwait(false);
            Trace.WriteLine("[GnomeShortcutWriter] Added the custom shortcut.");
        }
    }

    public async Task RemoveAsync(string id, CancellationToken ct)
    {
        var path = CustomPath(id);
        var list = ParseList(await GetAsync(MediaKeysSchema, ListKey, ct).ConfigureAwait(false));
        if (!list.Contains(path))
        {
            return;
        }

        await SetAsync(MediaKeysSchema, ListKey, FormatList(list.Where(entry => entry != path)), ct).ConfigureAwait(false);

        var schema = $"{CustomKeybindingSchema}:{path}";
        foreach (var key in new[] { "name", "command", "binding" })
        {
            // Unlisted, the values are inert; resetting only keeps dconf tidy.
            await DesktopSession.RunAsync(_processRunner, Gsettings, ["reset", schema, key], ct).ConfigureAwait(false);
        }
    }

    private static string CustomPath(string id) =>
        $"/org/gnome/settings-daemon/plugins/media-keys/custom-keybindings/{id.Replace('.', '-')}/";

    /// <summary>
    ///     Parses gsettings' list output: <c>@as []</c> or <c>['a', 'b']</c>, with single or double
    ///     quotes and backslash escapes. Anything else throws, so a misread list is never written back.
    /// </summary>
    internal static List<string> ParseList(string raw)
    {
        var text = raw.Trim();
        if (text.StartsWith("@as ", StringComparison.Ordinal))
        {
            text = text[4..].TrimStart();
        }

        if (text.Length < 2 || text[0] != '[' || text[^1] != ']')
        {
            throw new FormatException("gsettings returned a custom-keybindings value that is not a list.");
        }

        var items = new List<string>();
        var index = 1;
        while (true)
        {
            while (index < text.Length - 1 && (char.IsWhiteSpace(text[index]) || (items.Count > 0 && text[index] == ',')))
            {
                index++;
            }

            if (index >= text.Length - 1)
            {
                return items;
            }

            items.Add(ReadQuoted(text, ref index));
        }
    }

    private static string FormatList(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(FormatString)) + "]";

    private static string FormatString(string value) =>
        "'" + value.Replace(@"\", @"\\").Replace("'", @"\'") + "'";

    private static string ParseString(string raw)
    {
        var text = raw.Trim();
        var index = 0;
        return text.Length > 0 && text[0] is '\'' or '"' ? ReadQuoted(text, ref index) : text;
    }

    private static string ReadQuoted(string text, ref int index)
    {
        var quote = text[index];
        if (quote is not ('\'' or '"'))
        {
            throw new FormatException("gsettings returned an unquoted list entry.");
        }

        var value = new StringBuilder();
        for (index++; index < text.Length; index++)
        {
            var c = text[index];
            if (c == quote)
            {
                index++;
                return value.ToString();
            }

            if (c == '\\')
            {
                if (index + 1 >= text.Length || text[index + 1] is not ('\\' or '\'' or '"'))
                {
                    throw new FormatException("gsettings returned an escape Juvin cannot read back safely.");
                }

                c = text[++index];
            }

            value.Append(c);
        }

        throw new FormatException("gsettings returned an unterminated string.");
    }

    private async Task<string> GetAsync(string schema, string key, CancellationToken ct)
    {
        var result = await DesktopSession.RunAsync(_processRunner, Gsettings, ["get", schema, key], ct).ConfigureAwait(false);
        // ReSharper disable once ConvertIfStatementToReturnStatement -- a guard reads clearer than a throw expression in a ternary
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"gsettings get {key} exited with {result.ExitCode}.");
        }

        return result.StandardOutput;
    }

    private async Task SetAsync(string schema, string key, string value, CancellationToken ct)
    {
        var result = await DesktopSession.RunAsync(_processRunner, Gsettings, ["set", schema, key, value], ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"gsettings set {key} exited with {result.ExitCode}.");
        }
    }}
