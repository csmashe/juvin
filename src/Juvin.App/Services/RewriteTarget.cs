using System.Diagnostics;
using Juvin.Core.Connector;

namespace Juvin.App.Services;

/// <summary>The browser tab a capture came from, which Apply and Recapture go back to.</summary>
public interface IRewriteTarget
{
    /// <summary>
    ///     Captures again from the tab that owns <paramref name="editorId" />, or the shortcut's tab when it
    ///     is null; a closed connection or a timeout comes back as an error result.
    /// </summary>
    Task<ContextResult> RecaptureAsync(string? editorId, CancellationToken ct);

    /// <summary>Replaces the captured selection with <paramref name="text" />. Returns null on success.</summary>
    Task<ConnectorError?> ApplyAsync(CapturedContext capture, string text, CancellationToken ct);
}

/// <summary>An <see cref="IRewriteTarget" /> over the extension connection that sent the capture.</summary>
public sealed class ConnectorRewriteTarget : IRewriteTarget
{
    public const string TimeoutCode = "timeout";
    public const string TooLargeCode = "too-large";

    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(10);

    private readonly ConnectorSession _session;

    public ConnectorRewriteTarget(ConnectorSession session)
    {
        _session = session;
    }

    public async Task<ContextResult> RecaptureAsync(string? editorId, CancellationToken ct)
    {
        try
        {
            return await _session.RequestContextAsync(editorId, s_timeout, ct);
        }
        catch (Exception ex) when (ex is TimeoutException or ConnectorException or ConnectorFrameException)
        {
            Trace.WriteLine($"[RewriteTarget] Recapture failed ({ex.GetType().Name}).");
            return new ContextResult(null, ErrorOf(ex));
        }
    }

    public async Task<ConnectorError?> ApplyAsync(CapturedContext capture, string text, CancellationToken ct)
    {
        try
        {
            return await _session.ApplyAsync(
                capture.EditorId,
                capture.EditorHash,
                capture.SelectionStart,
                capture.SelectionEnd,
                text,
                s_timeout,
                ct
            );
        }
        catch (Exception ex) when (ex is TimeoutException or ConnectorException or ConnectorFrameException)
        {
            Trace.WriteLine($"[RewriteTarget] Apply failed ({ex.GetType().Name}).");
            return ErrorOf(ex);
        }
    }

    private static ConnectorError ErrorOf(Exception ex) =>
        ex switch
        {
            TimeoutException => new ConnectorError(TimeoutCode, "The extension did not answer in time."),
            ConnectorFrameException => new ConnectorError(TooLargeCode, "The request is too large for the connector."),
            _ => new ConnectorError(((ConnectorException)ex).Code, ex.Message),
        };
}
