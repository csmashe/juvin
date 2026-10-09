using System.Diagnostics;
using System.Text.Json;
using Juvin.Platform;

namespace Juvin.App.Services;

public enum ControlCommand
{
    Show,
    Ping,
}

/// <summary>
///     Answers requests on the app's control socket. Parsing and replies are pure; window
///     activation is left to whoever handles <see cref="ShowRequested" />.
/// </summary>
public sealed class ControlCommandHandler
{
    public const string ShowRequestLine = """{"v":1,"cmd":"show"}""";

    public event Action? ShowRequested;

    public Task<string> HandleAsync(string line, CancellationToken ct)
    {
        var (command, error) = Parse(line);
        // ReSharper disable once ConvertIfStatementToSwitchStatement -- a null guard and one side effect, not a dispatch
        if (command is null)
        {
            Trace.WriteLine($"[ControlCommandHandler] Rejected request ({error}, {line.Length} chars).");
            return Task.FromResult(LocalSocketProtocol.SerializeError(error!));
        }

        if (command == ControlCommand.Show)
        {
            ShowRequested?.Invoke();
        }

        return Task.FromResult(LocalSocketProtocol.SerializeOk());
    }

    /// <summary>Returns the command, or null and a protocol error code.</summary>
    internal static (ControlCommand? Command, string? Error) Parse(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (
                root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("v", out var version)
                || version.ValueKind != JsonValueKind.Number
                || !root.TryGetProperty("cmd", out var cmd)
                || cmd.ValueKind != JsonValueKind.String
            )
            {
                return (null, LocalSocketProtocol.ErrBadRequest);
            }

            if (!version.TryGetInt32(out var v) || v != LocalSocketProtocol.CurrentVersion)
            {
                return (null, LocalSocketProtocol.ErrUnsupportedVersion);
            }

            return cmd.GetString() switch
            {
                "show" => (ControlCommand.Show, null),
                "ping" => (ControlCommand.Ping, null),
                _ => (null, LocalSocketProtocol.ErrUnknownCommand),
            };
        }
        catch (JsonException)
        {
            return (null, LocalSocketProtocol.ErrBadRequest);
        }
    }
}
