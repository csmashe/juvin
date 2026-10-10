using Juvin.Core.Providers;

namespace Juvin.Platform.Tests.Fakes;

internal sealed class FakeProcessRunner(int exitCode = 0) : IProcessRunner
{
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
        return Task.FromResult(new ProcessResult(exitCode, "", ""));
    }
}
