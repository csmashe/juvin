using System.ComponentModel;
using System.Diagnostics;
using Juvin.Core.Providers;

namespace Juvin.Core.Tests;

public sealed class ProcessRunnerTests : IDisposable
{
    private static readonly TimeSpan s_deadline = TimeSpan.FromSeconds(10);

    private readonly TempDirectory _temp = new();
    private readonly ProcessRunner _runner = new();
    private int? _descendantPid;

    public void Dispose()
    {
        _runner.Shutdown();
        if (_descendantPid is { } pid)
        {
            try
            {
                using var descendant = Process.GetProcessById(pid);
                descendant.Kill();
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
            {
                // Already gone.
            }
        }

        _temp.Dispose();
    }

    [Fact]
    public async Task Shutdown_KillsTheRunningProcessTree()
    {
        var run = StartShellWithDescendant(CancellationToken.None);
        var descendantPid = await WaitForDescendantAsync(run);

        _runner.Shutdown();

        var result = await run.WaitAsync(s_deadline);
        Assert.NotEqual(0, result.ExitCode);
        await AssertExitsAsync(descendantPid);
    }

    [Fact]
    public async Task RunAsync_AfterShutdown_StartsNothing()
    {
        _runner.Shutdown();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => StartShellWithDescendant(CancellationToken.None));
    }

    [Fact]
    public async Task Cancel_KillsTheRunningProcessTree()
    {
        using var cts = new CancellationTokenSource();
        var run = StartShellWithDescendant(cts.Token);
        var descendantPid = await WaitForDescendantAsync(run);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(s_deadline, CancellationToken.None));
        await AssertExitsAsync(descendantPid);
    }

    private string PidFile => _temp.File("descendant.pid");

    /// <summary>A shell that writes its 30-second descendant's PID to <see cref="PidFile" /> and waits for it.</summary>
    private Task<ProcessResult> StartShellWithDescendant(CancellationToken ct) =>
        OperatingSystem.IsWindows()
            ? _runner.RunAsync(
                "powershell.exe",
                [
                    "-NoProfile",
                    "-NonInteractive",
                    "-Command",
                    $"$p = Start-Process ping.exe -ArgumentList '-n','30','127.0.0.1' -NoNewWindow -PassThru; Set-Content -LiteralPath '{PidFile.Replace("'", "''")}' -Value $p.Id; $p.WaitForExit()",
                ],
                null,
                null,
                ct
            )
            : _runner.RunAsync("/bin/sh", ["-c", "sleep 30 & echo $! > \"$1\"; wait", "sh", PidFile], null, null, ct);

    private async Task<int> WaitForDescendantAsync(Task run)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < s_deadline)
        {
            Assert.False(run.IsCompleted, "The shell ended before it started its descendant.");
            if (TryReadPid(out var pid))
            {
                _descendantPid = pid;
                return pid;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("The shell did not report its descendant's PID.");
    }

    private bool TryReadPid(out int pid)
    {
        pid = 0;
        try
        {
            return int.TryParse(File.ReadAllText(PidFile), out pid);
        }
        catch (IOException)
        {
            // Not written yet, or still being written.
            return false;
        }
    }

    private static async Task AssertExitsAsync(int pid)
    {
        var stopwatch = Stopwatch.StartNew();
        while (IsRunning(pid))
        {
            if (stopwatch.Elapsed > s_deadline)
            {
                throw new TimeoutException("The descendant is still running.");
            }

            await Task.Delay(50);
        }
    }

    // Where nothing reaps orphans, a killed descendant lingers as a zombie, so Linux reads its state instead.
    private static bool IsRunning(int pid)
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                var stat = File.ReadAllText($"/proc/{pid}/stat");
                return stat[stat.LastIndexOf(')') + 2] != 'Z';
            }
            catch (IOException)
            {
                return false;
            }
        }

        try
        {
            using var descendant = Process.GetProcessById(pid);
            return !descendant.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
