using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Juvin.Platform;

/// <summary>
///     The native messaging host manifest: one Gecko and one Chromium shape, identical apart from
///     the allowlist key. The per-OS registrars decide where it goes.
/// </summary>
public static class NativeHostManifest
{
    public const string FileName = NativeHost.Name + ".json";

    private static readonly JsonWriterOptions s_writerOptions = new()
    {
        Indented = true,
        // Keeps non-ASCII paths readable; the file is never embedded in HTML.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Render(BrowserFamily family, string hostPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostPath);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, s_writerOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("name", NativeHost.Name);
            writer.WriteString("description", NativeHost.Description);
            writer.WriteString("path", hostPath);
            writer.WriteString("type", "stdio");
            if (family == BrowserFamily.Firefox)
            {
                writer.WriteStartArray("allowed_extensions");
                writer.WriteStringValue(NativeHost.FirefoxExtensionId);
            }
            else
            {
                writer.WriteStartArray("allowed_origins");
                writer.WriteStringValue(NativeHost.ChromeOrigin);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    /// <summary>
    ///     Compares the <c>path</c> in the manifest at <paramref name="manifestPath" /> with
    ///     <paramref name="expectedHostPath" />. A manifest that cannot be read, or any manifest
    ///     when there is no expected path, is <see cref="HostRegistration.Stale" />.
    /// </summary>
    public static HostRegistration Check(string manifestPath, string? expectedHostPath)
    {
        if (!File.Exists(manifestPath))
        {
            return HostRegistration.NotRegistered;
        }

        if (expectedHostPath is null)
        {
            return HostRegistration.Stale;
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(ReadHostPath(manifestPath), expectedHostPath, comparison)
            ? HostRegistration.Registered
            : HostRegistration.Stale;
    }

    /// <summary>The manifest's <c>path</c>, or null when the file is missing or not a manifest.</summary>
    public static string? ReadHostPath(string manifestPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("path", out var path)
                && path.ValueKind == JsonValueKind.String
                    ? path.GetString()
                    : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>The host path, or a user-readable failure when the app could not find its host executable.</summary>
    public static string RequireHost(string? hostPath)
    {
        return hostPath
            ?? throw new NativeHostRegistrationException(
                "The Juvin browser connector (juvin-host) was not found next to the app. Reinstall Juvin and try again."
            );
    }
}
