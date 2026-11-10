using Juvin.Core.Providers;

namespace Juvin.Core.Tests;

public sealed class ProcessRunnerTests
{
    private static readonly TimeSpan s_deadline = TimeSpan.FromSeconds(10);

    // The 30-second grandchild holds the output pipe open, so a run ends early only if the whole tree is gone.
    private static readonly (string FileName, string[] Arguments) s_shellWithChild = OperatingSystem.IsWindows()
        ? ("cmd.exe", ["/d", "/c", "ping -n 30 127.0.0.1"])
        : ("/bin/sh", ["-c", "sleep 30 & wait"]);

    [Fact]
    public async Task Shutdown_KillsTheRunningProcessTree()
    {
        var runner = new ProcessRunner();
        var run = runner.RunAsync(s_shellWithChild.FileName, s_shellWithChild.Arguments, null, null, CancellationToken.None);

        runner.Shutdown();

        var result = await run.WaitAsync(s_deadline);
        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task RunAsync_AfterShutdown_StartsNothing()
    {
        var runner = new ProcessRunner();
        runner.Shutdown();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync(s_shellWithChild.FileName, s_shellWithChild.Arguments, null, null, CancellationToken.None)
        );
    }

    [Fact]
    public async Task Cancel_KillsTheRunningProcessTree()
    {
        var runner = new ProcessRunner();
        using var cts = new CancellationTokenSource();
        var run = runner.RunAsync(s_shellWithChild.FileName, s_shellWithChild.Arguments, null, null, cts.Token);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(s_deadline, CancellationToken.None));
    }
}
