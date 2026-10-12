using System.Diagnostics;
using System.Globalization;

namespace Juvin.Platform.Linux.Hotkeys.Evdev;

/// <summary>Whether the current process carries the <c>input</c> group; null when that cannot be told.</summary>
public static class InputGroupCheck
{
    public static bool? CurrentUserInInputGroup() =>
        IsInInputGroup(ReadLines("/etc/group"), ReadLines("/proc/self/status"));

    /// <param name="groupFile">Lines of <c>/etc/group</c>, such as <c>input:x:104:alice</c>.</param>
    /// <param name="status">Lines of <c>/proc/self/status</c>; the <c>Groups:</c> line lists the process's group ids.</param>
    internal static bool? IsInInputGroup(IEnumerable<string>? groupFile, IEnumerable<string>? status)
    {
        var inputGid = groupFile?
            .Select(line => line.Split(':'))
            .Where(fields => fields is ["input", _, _, ..])
            .Select(fields => int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var gid) ? gid : (int?)null)
            .FirstOrDefault(gid => gid is not null);
        var groups = status?.FirstOrDefault(line => line.StartsWith("Groups:", StringComparison.Ordinal));
        if (inputGid is null || groups is null)
        {
            return null;
        }

        return groups["Groups:".Length..]
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Contains(inputGid.Value.ToString(CultureInfo.InvariantCulture));
    }

    private static string[]? ReadLines(string path)
    {
        try
        {
            return File.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"[InputGroupCheck] Reading {path} failed ({ex.GetType().Name}).");
            return null;
        }
    }
}
