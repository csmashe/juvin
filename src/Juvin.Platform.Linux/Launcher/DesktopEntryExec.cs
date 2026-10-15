using System.Text;

namespace Juvin.Platform.Linux.Launcher;

internal static class DesktopEntryExec
{
    /// <summary>
    ///     Quotes a path as one Exec argument: inside quotes, <c>"</c>, <c>`</c>, <c>$</c>, and
    ///     <c>\</c> take a backslash, then the value's own escaping doubles every backslash again.
    ///     A literal <c>%</c> is written <c>%%</c>.
    /// </summary>
    public static string Quote(string path)
    {
        var quoted = new StringBuilder("\"");
        foreach (var c in path)
        {
            switch (c)
            {
                case '"' or '`' or '$':
                    quoted.Append(@"\\").Append(c);
                    break;
                case '\\':
                    quoted.Append(@"\\\\");
                    break;
                case '%':
                    quoted.Append("%%");
                    break;
                default:
                    quoted.Append(c);
                    break;
            }
        }

        return quoted.Append('"').ToString();
    }
}
