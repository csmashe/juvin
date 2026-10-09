namespace Juvin.Platform;

public static class SecretName
{
    private const int MaxLength = 64;

    private static readonly HashSet<string> s_reservedDeviceNames =
    [
        "con", "prn", "aux", "nul",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    ];

    /// <summary>
    ///     Secret names double as file names on some platforms, so they are limited to lowercase
    ///     ASCII letters, digits, '.', '-' and '_', must start and end with a letter or digit, and
    ///     may not be a DOS device name. Lowercase only, because case-insensitive file systems
    ///     would map "token" and "Token" to one file.
    /// </summary>
    public static void ThrowIfInvalid(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (
            name.Length > MaxLength
            || !IsLetterOrDigit(name[0])
            || !IsLetterOrDigit(name[^1])
            || !name.All(c => IsLetterOrDigit(c) || c is '.' or '-' or '_')
            || s_reservedDeviceNames.Contains(name.Split('.')[0])
        )
        {
            throw new ArgumentException($"'{name}' is not a valid secret name.", nameof(name));
        }
    }

    private static bool IsLetterOrDigit(char c) => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c);
}
