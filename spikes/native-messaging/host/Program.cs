using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// Throwaway native messaging host for the Phase 0 spike.
// Reads length-prefixed JSON on stdin, answers "hello" with "pong", logs every step to a file.

var logDir = Path.Combine(Path.GetTempPath(), "juvin-nm-spike");
Directory.CreateDirectory(logDir);
var logPath = Path.Combine(logDir, $"host-{Environment.ProcessId}.log");

void Log(string line)
{
    File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} {line}\n");
}

Log($"start pid={Environment.ProcessId} ppid={GetParentPid()} args=[{string.Join(" ", args)}] cwd={Environment.CurrentDirectory}");
Log($"env FLATPAK_ID={Environment.GetEnvironmentVariable("FLATPAK_ID")} XDG_SESSION_TYPE={Environment.GetEnvironmentVariable("XDG_SESSION_TYPE")} parent={GetParentName()}");

using var stdin = Console.OpenStandardInput();
using var stdout = Console.OpenStandardOutput();

while (true)
{
    var lengthBytes = new byte[4];
    if (!ReadExactly(stdin, lengthBytes))
    {
        Log("stdin closed, exiting");
        break;
    }

    var length = BitConverter.ToInt32(lengthBytes);
    var payload = new byte[length];
    ReadExactly(stdin, payload);
    var message = JsonNode.Parse(payload)!.AsObject();
    var type = message["type"]?.GetValue<string>();
    Log($"recv type={type} bytes={length} json={message.ToJsonString()}");

    if (type == "hello")
    {
        Send(new JsonObject
        {
            ["type"] = "pong",
            ["hostPid"] = Environment.ProcessId,
            ["hostParent"] = GetParentName(),
            ["flatpakId"] = Environment.GetEnvironmentVariable("FLATPAK_ID"),
            ["logPath"] = logPath,
            ["sentAt"] = message["sentAt"]?.DeepClone(),
        });
    }
    else if (type == "ack")
    {
        Log($"round trip complete: {message["roundTripMs"]} ms");
        Send(new JsonObject { ["type"] = "bye" });
    }
}

void Send(JsonObject message)
{
    var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
    stdout.Write(BitConverter.GetBytes(bytes.Length));
    stdout.Write(bytes);
    stdout.Flush();
    Log($"sent type={message["type"]} bytes={bytes.Length}");
}

static bool ReadExactly(Stream stream, byte[] buffer)
{
    var offset = 0;
    while (offset < buffer.Length)
    {
        var read = stream.Read(buffer, offset, buffer.Length - offset);
        if (read == 0)
        {
            return false;
        }
        offset += read;
    }
    return true;
}

static int GetParentPid()
{
    try
    {
        var stat = File.ReadAllText("/proc/self/stat");
        var parts = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
        return int.Parse(parts[1]);
    }
    catch
    {
        return -1;
    }
}

static string GetParentName()
{
    try
    {
        return File.ReadAllText($"/proc/{GetParentPid()}/cmdline").Replace('\0', ' ').Trim();
    }
    catch
    {
        return "?";
    }
}
