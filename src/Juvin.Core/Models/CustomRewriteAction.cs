using System.Security.Cryptography;

namespace Juvin.Core.Models;

/// <summary>A rewrite action the user defined. Entries with a blank name or instruction are kept but not offered.</summary>
public sealed record CustomRewriteAction(string Id, string Name, string Instruction)
{
    private const string IdPrefix = "custom-";

    /// <summary>A fresh id that differs from every built-in id and from <paramref name="taken" />.</summary>
    public static string NewId(IEnumerable<string> taken)
    {
        var used = taken.ToHashSet(StringComparer.OrdinalIgnoreCase);
        string id;
        do
        {
            id = IdPrefix + RandomNumberGenerator.GetHexString(8, lowercase: true);
        }
        while (used.Contains(id));

        return id;
    }
}
