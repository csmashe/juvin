using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Juvin.Core.Providers;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

public interface IProcessRunner
{
    /// <summary>
    ///     Runs a child process to completion, writing <paramref name="standardInput" /> and then
    ///     closing its input. Cancelling kills the whole process tree.
    /// </summary>
    /// <exception cref="Win32Exception">The executable could not be started.</exception>
    /// <exception cref="ArgumentException">An argument cannot be passed through the Windows command interpreter.</exception>
    Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? standardInput,
        string? workingDirectory,
        CancellationToken ct
    );
}

public sealed class ProcessRunner : IProcessRunner
{
    private static readonly SearchValues<char> s_cmdMetacharacters = SearchValues.Create("\"%!^&|<>");
    private static readonly UTF8Encoding s_utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? standardInput,
        string? workingDirectory,
        CancellationToken ct
    )
    {
        var startInfo = BuildStartInfo(fileName, arguments, OperatingSystem.IsWindows());
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.StandardInputEncoding = s_utf8;
        startInfo.StandardOutputEncoding = s_utf8;
        startInfo.StandardErrorEncoding = s_utf8;
        startInfo.WorkingDirectory = workingDirectory ?? "";

        using var process = new Process();
        process.StartInfo = startInfo;
        process.Start();

        // Both pipes drain from the start so a chatty child cannot block on a full buffer.
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await WriteInputAsync(process, standardInput, ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            // A descendant can keep a pipe open after the child exits, so the drains are supervised too.
            return new ProcessResult(process.ExitCode, await stdout.WaitAsync(ct).ConfigureAwait(false), await stderr.WaitAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }
    }

    /// <summary>
    ///     A Windows batch shim (the npm <c>codex.cmd</c>) cannot be started directly; it has to go
    ///     through the command interpreter, which means quoting the command line by hand.
    /// </summary>
    internal static ProcessStartInfo BuildStartInfo(string fileName, IReadOnlyList<string> arguments, bool isWindows)
    {
        var extension = Path.GetExtension(fileName);
        if (!isWindows || !(extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            var direct = new ProcessStartInfo(fileName);
            foreach (var argument in arguments)
            {
                direct.ArgumentList.Add(argument);
            }

            return direct;
        }

        var tokens = arguments.Prepend(fileName).Select(QuoteForCmd);
        return new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
        {
            Arguments = "/d /s /c \"" + string.Join(' ', tokens) + "\"",
        };
    }

    private static string QuoteForCmd(string token)
    {
        if (token.AsSpan().ContainsAny(s_cmdMetacharacters))
        {
            throw new ArgumentException("Argument contains characters that cannot be passed through cmd.exe.", nameof(token));
        }

        return token.Length == 0 || token.Any(char.IsWhiteSpace) ? "\"" + token + "\"" : token;
    }

    private static async Task WriteInputAsync(Process process, string? input, CancellationToken ct)
    {
        try
        {
            if (input is not null)
            {
                await process.StandardInput.WriteAsync(input.AsMemory(), ct).ConfigureAwait(false);
            }

            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The child exited without reading all of its input; its exit code tells the story.
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // Already exited.
        }
    }
}
