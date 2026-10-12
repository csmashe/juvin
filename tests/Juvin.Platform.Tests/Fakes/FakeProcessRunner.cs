using Juvin.Core.Providers;

namespace Juvin.Platform.Tests.Fakes;

internal sealed class FakeProcessRunner(int exitCode = 0) : IProcessRunner
{
    public int ExitCode { get; set; } = exitCode;

    /// <summary>When set, decides the result of each call; otherwise every call succeeds with empty output.</summary>
    public Func<string, IReadOnlyList<string>, ProcessResult>? Handler { get; set; }

    public List<(string FileName, IReadOnlyList<string> Arguments)> Calls { get; } = [];

    public Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? standardInput,
        string? workingDirectory,
        CancellationToken ct
    )
    {
        Calls.Add((fileName, arguments));
        return Task.FromResult(Handler?.Invoke(fileName, arguments) ?? new ProcessResult(ExitCode, "", ""));
    }
}
