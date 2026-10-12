using Juvin.Core.Providers;

namespace Juvin.Platform.Tests.Fakes;

/// <summary>An in-memory dconf behind <c>gsettings get/set/reset</c>; values are stored as gsettings prints them.</summary>
internal sealed class FakeGsettings : IProcessRunner
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

    private List<IReadOnlyList<string>> Calls { get; } = [];

    public int SetCount => Calls.Count(call => call[0] == "set");

    public Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? standardInput,
        string? workingDirectory,
        CancellationToken ct
    )
    {
        Assert.Equal("gsettings", fileName);
        Calls.Add(arguments);
        var key = arguments[1] + " " + arguments[2];
        switch (arguments[0])
        {
            case "get":
                return Result(Values.GetValueOrDefault(key, arguments[2] == "custom-keybindings" ? "@as []" : "''") + "\n");
            case "set":
                Values[key] = arguments[3];
                return Result("");
            default:
                Values.Remove(key);
                return Result("");
        }
    }

    private static Task<ProcessResult> Result(string output) => Task.FromResult(new ProcessResult(0, output, ""));
}
