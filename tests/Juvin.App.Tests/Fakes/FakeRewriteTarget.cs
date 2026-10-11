using Juvin.App.Services;
using Juvin.Core.Connector;

namespace Juvin.App.Tests.Fakes;

internal sealed class FakeRewriteTarget : IRewriteTarget
{
    public List<(CapturedContext Capture, string Text)> Applied { get; } = [];

    /// <summary>Returned by the next apply; null means it succeeds.</summary>
    public ConnectorError? ApplyError { get; set; }

    /// <summary>Returned by every recapture.</summary>
    public ContextResult? NextCapture { get; set; }

    /// <summary>When set, recaptures wait for it instead of returning <see cref="NextCapture" />, ignoring cancellation.</summary>
    public TaskCompletionSource<ContextResult>? RecaptureGate { get; set; }

    /// <summary>The editor id each recapture asked for.</summary>
    public List<string?> RecaptureEditorIds { get; } = [];

    public CancellationToken LastRecaptureToken { get; private set; }

    public Task<ContextResult> RecaptureAsync(string? editorId, CancellationToken ct)
    {
        RecaptureEditorIds.Add(editorId);
        LastRecaptureToken = ct;
        return RecaptureGate?.Task
            ?? Task.FromResult(NextCapture ?? throw new InvalidOperationException("No capture scripted."));
    }

    public Task<ConnectorError?> ApplyAsync(CapturedContext capture, string text, CancellationToken ct)
    {
        Applied.Add((capture, text));
        return Task.FromResult(ApplyError);
    }
}
