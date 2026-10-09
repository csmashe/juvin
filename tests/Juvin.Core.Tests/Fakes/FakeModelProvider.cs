using System.Runtime.CompilerServices;
using Juvin.Core.Models;
using Juvin.Core.Providers;

namespace Juvin.Core.Tests.Fakes;

internal sealed class FakeModelProvider : IModelProvider
{
    public FakeModelProvider(ProcessingMode mode)
    {
        Mode = mode;
    }

    public List<ChatRequest> Requests { get; } = [];

    public Func<ChatRequest, string> Reply { get; set; } = _ => "rewritten";

    public Exception? FailAfterPartial { get; set; }

    public ProcessingMode Mode { get; }

    public string DisplayName => "Fake " + Mode;

    public bool SupportsStreaming => true;

    public bool RunsLocally => Mode == ProcessingMode.Local;

    public Task<ProviderStatus> ProbeAsync(CancellationToken ct) =>
        Task.FromResult(new ProviderStatus(ProviderStatusKind.Ready, "Provider.Status.Ready", []));

    public async IAsyncEnumerable<string> StreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        yield return (await CompleteAsync(request, null, ct)).Text;
    }

    public Task<ChatResult> CompleteAsync(ChatRequest request, IProgress<string>? preview, CancellationToken ct)
    {
        Requests.Add(request);
        var text = Reply(request);
        preview?.Report(text[..(text.Length / 2)]);
        if (FailAfterPartial is not null)
        {
            throw FailAfterPartial;
        }

        preview?.Report(text);
        return Task.FromResult(new ChatResult(text, request.Model ?? "fake-model", 100, 20));
    }
}
