using System.Diagnostics;
using System.Text.Json;
using Juvin.Platform;

namespace Juvin.App.Services;

public enum ControlCommand
{
    Show,
    Ping,
    Trigger,
}

/// <summary>
///     Answers requests on the app's control socket. Parsing and replies are pure; window
///     activation and captures are left to whoever handles <see cref="ShowRequested" /> and
///     <see cref="TriggerRequested" />.
/// </summary>
public sealed class ControlCommandHandler
{
    public const string ShowRequestLine = """{"v":1,"cmd":"show"}""";

    /// <summary>Sent by <c>juvin --trigger</c>, which a desktop environment's own shortcut runs.</summary>
    public const string TriggerRequestLine = """{"v":1,"cmd":"trigger"}""";

    public event Action? ShowRequested;

    public event Action? TriggerRequested;

    public Task<string> HandleAsync(string line, CancellationToken ct)
    {
        var (command, error) = Parse(line);
        // ReSharper disable once ConvertIfStatementToSwitchStatement -- the rejection is a guard clause; Ping needs no dispatch
        if (command is null)
        {
            Trace.WriteLine($"[ControlCommandHandler] Rejected request ({error}, {line.Length} chars).");
            return Task.FromResult(LocalSocketProtocol.SerializeError(error!));
        }

        if (command == ControlCommand.Show)
        {
            ShowRequested?.Invoke();
        }
        else if (command == ControlCommand.Trigger)
        {
            TriggerRequested?.Invoke();
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
                "trigger" => (ControlCommand.Trigger, null),
                _ => (null, LocalSocketProtocol.ErrUnknownCommand),
            };
        }
        catch (JsonException)
        {
            return (null, LocalSocketProtocol.ErrBadRequest);
        }
    }
}
