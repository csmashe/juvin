using Juvin.Core.Providers;

namespace Juvin.Core.Tests.Fakes;

internal sealed class FakeProcessRunner : IProcessRunner
{
    private readonly Func<IReadOnlyList<string>, ProcessResult> _respond;

    public FakeProcessRunner(Func<IReadOnlyList<string>, ProcessResult> respond)
    {
        _respond = respond;
    }

    public List<(string FileName, IReadOnlyList<string> Arguments, string? Input, string? WorkingDirectory)> Calls { get; } = [];

    public Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? standardInput,
        string? workingDirectory,
        CancellationToken ct
    )
    {
        Calls.Add((fileName, arguments, standardInput, workingDirectory));
        return Task.FromResult(_respond(arguments));
    }
}
