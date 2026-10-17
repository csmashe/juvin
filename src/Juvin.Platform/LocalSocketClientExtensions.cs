using System.Diagnostics;

namespace Juvin.Platform;

public static class LocalSocketClientExtensions
{
    private static readonly TimeSpan s_retryDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>
    ///     Retries <see cref="ILocalSocketClient.SendAsync" /> until a reply arrives or <paramref name="budget" />
    ///     is spent, so a server that holds the instance lock but has not bound its endpoint yet still answers.
    /// </summary>
    public static async Task<string?> SendWhenReadyAsync(this ILocalSocketClient client, string line, TimeSpan budget)
    {
        ArgumentNullException.ThrowIfNull(client);

        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            // Read once per attempt: a delay can resume past the deadline, and a negative timeout would throw.
            var remaining = budget - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
            {
                return null;
            }

            var reply = await client.SendAsync(line, remaining).ConfigureAwait(false);
            if (reply is not null)
            {
                return reply;
            }

            var left = budget - Stopwatch.GetElapsedTime(started);
            if (left <= TimeSpan.Zero)
            {
                return null;
            }

            await Task.Delay(left < s_retryDelay ? left : s_retryDelay).ConfigureAwait(false);
        }
    }
}
