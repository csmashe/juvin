using System.Net;
using System.Text.Json;
using Juvin.Core.Models;
using Juvin.Core.Providers;
using Juvin.Core.Tests.Fakes;

namespace Juvin.Core.Tests;

public sealed class OpenAiCompatibleProviderTests
{
    private const string OllamaChat =
        """
        {"model":"qwen3.5:27b","message":{"role":"assistant","content":"","thinking":"secret plan"},"done":false}
        {"model":"qwen3.5:27b","message":{"role":"assistant","content":"Hi Dana,"},"done":false}
        {"model":"qwen3.5:27b","message":{"role":"assistant","content":" thanks.\n\n"},"done":false}
        {"model":"qwen3.5:27b","message":{"role":"assistant","content":""},"done":true,"prompt_eval_count":766,"eval_count":92}
        """;

    private const string OpenAiChat =
        """
        data: {"model":"local-model","choices":[{"index":0,"delta":{"reasoning":"hidden"}}]}

        data: {"model":"local-model","choices":[{"index":0,"delta":{"reasoning_content":"also hidden"}}]}

        data: {"model":"local-model","choices":[{"index":0,"delta":{"content":"<thi"}}]}

        data: {"model":"local-model","choices":[{"index":0,"delta":{"content":"nk>x</think>Hello"}}]}

        data: {"model":"local-model","choices":[{"index":0,"delta":{"content":" there"},"finish_reason":"stop"}]}

        data: {"model":"local-model","choices":[],"usage":{"prompt_tokens":12,"completion_tokens":5}}

        data: [DONE]


        """;

    private static readonly ChatRequest s_request = new()
    {
        SystemPrompt = "system",
        Messages = [new ChatMessage(ChatRole.User, "draft")],
        DisableThinking = true,
    };

    [Fact]
    public async Task Ollama_StreamsContentDropsThinkingAndReadsTokenCounts()
    {
        var handler = Ollama().Route("/api/chat", OllamaChat);
        var preview = new List<string>();

        var result = await Provider(handler, "qwen3.5:27b")
            .CompleteAsync(s_request, new Rewrite.InlineProgress<string>(preview.Add), CancellationToken.None);

        Assert.Equal("Hi Dana, thanks.", result.Text);
        Assert.Equal("qwen3.5:27b", result.Model);
        Assert.Equal(766, result.InputTokens);
        Assert.Equal(92, result.OutputTokens);
        Assert.StartsWith("Hi Dana, thanks.", preview.Last());

        using var body = JsonDocument.Parse(handler.BodyFor("/api/chat"));
        Assert.False(body.RootElement.GetProperty("think").GetBoolean());
        Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal(0.3, body.RootElement.GetProperty("options").GetProperty("temperature").GetDouble());
        var messages = body.RootElement.GetProperty("messages");
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.False(body.RootElement.TryGetProperty("keep_alive", out _));
    }

    [Fact]
    public async Task Ollama_LeavesThinkUnsetWhenThinkingIsAllowed()
    {
        var handler = Ollama().Route("/api/chat", OllamaChat);

        await Provider(handler, "m").CompleteAsync(s_request with { DisableThinking = false }, null, CancellationToken.None);

        Assert.DoesNotContain("\"think\"", handler.BodyFor("/api/chat"));
    }

    [Fact]
    public async Task Ollama_FailsWhenTheStreamEndsBeforeDone()
    {
        var handler = Ollama().Route("/api/chat", OllamaChat.Split('\n')[1]);

        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            Provider(handler, "m").CompleteAsync(s_request, null, CancellationToken.None)
        );

        Assert.Equal(ProviderFailureKind.Protocol, ex.Kind);
    }

    [Fact]
    public async Task Ollama_RefusesACloudModelBeforeSendingAnything()
    {
        var handler = Ollama().Route("/api/chat", OllamaChat);

        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            Provider(handler, "gpt-oss:120b-cloud").CompleteAsync(s_request, null, CancellationToken.None)
        );

        Assert.Equal(ProviderFailureKind.Configuration, ex.Kind);
        Assert.DoesNotContain(handler.Requests, r => r.Path == "/api/chat");
    }

    [Theory]
    [InlineData("library/gpt-oss:120b-cloud")]
    [InlineData("registry.ollama.ai/library/gpt-oss:120b-cloud")]
    public async Task Ollama_RefusesACloudModelUnderAnyAlias(string alias)
    {
        var handler = Ollama().Route("/api/chat", OllamaChat);

        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            Provider(handler, alias).CompleteAsync(s_request, null, CancellationToken.None)
        );

        Assert.Equal(ProviderFailureKind.Configuration, ex.Kind);
        Assert.DoesNotContain(handler.Requests, r => r.Path == "/api/chat");
    }

    [Fact]
    public async Task Ollama_RefusesAnEmbeddingModelAndAnUnknownModelBeforeChatting()
    {
        var handler = Ollama().Route("/api/chat", OllamaChat);

        var embedding = await Assert.ThrowsAsync<ProviderException>(() =>
            Provider(handler, "nomic-embed-text").CompleteAsync(s_request, null, CancellationToken.None)
        );
        var unknown = await Assert.ThrowsAsync<ProviderException>(() =>
            Provider(handler, "nope").CompleteAsync(s_request, null, CancellationToken.None)
        );

        Assert.Equal(ProviderFailureKind.Configuration, embedding.Kind);
        Assert.Equal(ProviderFailureKind.Configuration, unknown.Kind);
        Assert.DoesNotContain(handler.Requests, r => r.Path == "/api/chat");
    }

    [Fact]
    public async Task Ollama_ReportsAMalformedModelDescriptionAsAProtocolFailure()
    {
        var handler = Ollama().RouteByBody("/api/show", _ => "[]").Route("/api/chat", OllamaChat);

        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            Provider(handler, "m").CompleteAsync(s_request, null, CancellationToken.None)
        );

        Assert.Equal(ProviderFailureKind.Protocol, ex.Kind);
    }

    [Fact]
    public async Task Ollama_FillsCapabilitiesFromShowWhenTagsOmitThem()
    {
        var handler = new FakeHttpHandler()
            .Route("/api/version", """{"version":"0.12.3"}""")
            .Route("/api/tags", """{"models":[{"name":"nomic-embed-text:latest"},{"name":"qwen3:8b","details":{"parameter_size":"8.2B"}}]}""")
            .RouteByBody("/api/show", body => body.Contains("embed") ? """{"capabilities":["embedding"]}""" : """{"capabilities":["completion"]}""");

        var status = await Provider(handler, "qwen3:8b").ProbeAsync(CancellationToken.None);

        Assert.Equal(ProviderStatusKind.Ready, status.Kind);
        Assert.Equal([false, true], status.Models.Select(m => m.SupportsChat));
        Assert.Equal(2, handler.Requests.Count(r => r.Path == "/api/show"));
    }

    [Fact]
    public async Task Ollama_SizesTheContextWindowToTheConversation()
    {
        var handler = Ollama().Route("/api/chat", OllamaChat);
        var request = s_request with { Messages = [new ChatMessage(ChatRole.User, new string('x', 20_000))] };

        await Provider(handler, "m").CompleteAsync(request, null, CancellationToken.None);

        using var body = JsonDocument.Parse(handler.BodyFor("/api/chat"));
        Assert.Equal(16384, body.RootElement.GetProperty("options").GetProperty("num_ctx").GetInt32());
        Assert.Equal(8192, OpenAiCompatibleProvider.ContextWindowFor(s_request));
        Assert.Throws<ProviderException>(() =>
            OpenAiCompatibleProvider.ContextWindowFor(
                s_request with { Messages = [new ChatMessage(ChatRole.User, new string('x', 70_000))] }
            )
        );
    }

    [Fact]
    public async Task Ollama_ReportsACloudModelAsNotUsable()
    {
        var status = await Provider(Ollama(), "gpt-oss:120b-cloud").ProbeAsync(CancellationToken.None);

        Assert.Equal(ProviderStatusKind.NoModel, status.Kind);
        Assert.Equal("Provider.Status.RemoteModel", status.MessageKey);
        Assert.Equal([false, false, false, true, false], status.Models.Select(m => m.IsRemote));
        Assert.Equal([true, true, true, true, false], status.Models.Select(m => m.SupportsChat));
    }

    [Fact]
    public async Task Ollama_ReportsAnEmbeddingModelAsNotUsable()
    {
        var status = await Provider(Ollama(), "nomic-embed-text").ProbeAsync(CancellationToken.None);

        Assert.Equal(ProviderStatusKind.NoModel, status.Kind);
        Assert.Equal("Provider.Status.NotAChatModel", status.MessageKey);
    }

    [Fact]
    public async Task Ollama_ChecksTheModelAgainOnEveryTurn()
    {
        var handler = Ollama().Route("/api/chat", OllamaChat);
        var provider = Provider(handler, "m");

        await provider.CompleteAsync(s_request, null, CancellationToken.None);
        await provider.CompleteAsync(s_request, null, CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count(r => r.Path == "/api/show"));
    }

    [Fact]
    public async Task Ollama_FailsWhenTheReplyHitTheOutputLimit()
    {
        var handler = Ollama().Route(
            "/api/chat",
            """
            {"model":"m","message":{"role":"assistant","content":"Hi Dana, I will"},"done":false}
            {"model":"m","message":{"role":"assistant","content":""},"done":true,"done_reason":"length"}
            """
        );

        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            Provider(handler, "m").CompleteAsync(s_request, null, CancellationToken.None)
        );

        Assert.Equal(ProviderFailureKind.Protocol, ex.Kind);
    }

    [Theory]
    [InlineData("length")]
    [InlineData("content_filter")]
    public async Task OpenAi_FailsWhenTheReplyWasCutShort(string reason)
    {
        var handler = new FakeHttpHandler().Route(
            "/v1/chat/completions",
            $$"""
            data: {"choices":[{"delta":{"content":"Hi Dana, I will"},"finish_reason":"{{reason}}"}]}

            data: [DONE]


            """
        );

        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            Provider(handler, "m").CompleteAsync(s_request, null, CancellationToken.None)
        );

        Assert.Equal(ProviderFailureKind.Protocol, ex.Kind);
    }

    [Fact]
    public async Task Stream_TimesOutWhenTheBodyStallsAfterTheHeaders()
    {
        var handler = new FakeHttpHandler().Stall("/v1/chat/completions");
        var provider = new OpenAiCompatibleProvider(
            new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(200) },
            new AppSettings { LocalServerUrl = "http://localhost:1234", LocalModel = "m" }
        );

        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            provider.CompleteAsync(s_request, null, CancellationToken.None)
        );

        Assert.Equal(ProviderFailureKind.Unreachable, ex.Kind);
    }

    [Fact]
    public async Task Stream_StaysAliveWhileDeltasKeepArrivingSlowerThanTheTimeoutInTotal()
    {
        // Eight gaps of 150 ms add up to more than the 1 s idle timeout, while each gap leaves a
        // wide margin for a slow CI runner.
        var handler = new FakeHttpHandler().Trickle(
            "/v1/chat/completions",
            TimeSpan.FromMilliseconds(150),
            "data: {\"choices\":[{\"delta\":{\"content\":\"a\"}}]}\n\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"b\"}}]}\n\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"c\"}}]}\n\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"d\"}}]}\n\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"e\"}}]}\n\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"f\"}}]}\n\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"g\"}}]}\n\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"h\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n"
        );
        var provider = new OpenAiCompatibleProvider(
            new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(1) },
            new AppSettings { LocalServerUrl = "http://localhost:1234", LocalModel = "m" }
        );

        var result = await provider.CompleteAsync(s_request, null, CancellationToken.None);

        Assert.Equal("abcdefgh", result.Text);
    }

    [Fact]
    public async Task OpenAi_ReasoningChunksKeepTheStreamAliveWithoutReachingTheReply()
    {
        var reasoning = Enumerable.Repeat("data: {\"choices\":[{\"delta\":{\"reasoning\":\"r\"}}]}\n\n", 8);
        var handler = new FakeHttpHandler().Trickle(
            "/v1/chat/completions",
            TimeSpan.FromMilliseconds(150),
            [.. reasoning, "data: {\"choices\":[{\"delta\":{\"content\":\"done\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n"]
        );
        var provider = new OpenAiCompatibleProvider(
            new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(1) },
            new AppSettings { LocalServerUrl = "http://localhost:1234", LocalModel = "m" }
        );

        var result = await provider.CompleteAsync(s_request, null, CancellationToken.None);

        Assert.Equal("done", result.Text);
    }

    [Fact]
    public async Task Ollama_ThinkingChunksKeepTheStreamAliveWithoutReachingTheReply()
    {
        var thinking = Enumerable.Repeat("{\"message\":{\"role\":\"assistant\",\"content\":\"\",\"thinking\":\"t\"},\"done\":false}\n", 8);
        var handler = Ollama().Trickle(
            "/api/chat",
            TimeSpan.FromMilliseconds(150),
            [.. thinking, "{\"message\":{\"role\":\"assistant\",\"content\":\"done\"},\"done\":true}\n"]
        );
        var provider = new OpenAiCompatibleProvider(
            new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(1) },
            new AppSettings { LocalServerUrl = "http://localhost:11434", LocalModel = "m" }
        );

        var result = await provider.CompleteAsync(s_request, null, CancellationToken.None);

        Assert.Equal("done", result.Text);
    }

    [Fact]
    public async Task OpenAi_RefusesAConversationTooLongForTheContextBudget()
    {
        var handler = new FakeHttpHandler().Route("/v1/chat/completions", OpenAiChat);
        var request = s_request with { Messages = [new ChatMessage(ChatRole.User, new string('x', 70_000))] };

        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            Provider(handler, "m").CompleteAsync(request, null, CancellationToken.None)
        );

        Assert.Equal(ProviderFailureKind.Configuration, ex.Kind);
        Assert.DoesNotContain(handler.Requests, r => r.Path == "/v1/chat/completions");
    }

    [Fact]
    public async Task OpenAi_AReplyEndingInsideAReasoningBlockIsAProtocolFailure()
    {
        var handler = new FakeHttpHandler().Route(
            "/v1/chat/completions",
            """
            data: {"choices":[{"delta":{"content":"<think>private reasoning"},"finish_reason":"stop"}]}

            data: [DONE]


            """
        );

        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            Provider(handler, "m").CompleteAsync(s_request, null, CancellationToken.None)
        );

        Assert.Equal(ProviderFailureKind.Protocol, ex.Kind);
        Assert.Equal("The model's reply ended inside a reasoning block.", ex.Message);
    }

    [Fact]
    public async Task Probe_TimesOutWhenTheBodyStallsAfterTheHeaders()
    {
        var handler = new FakeHttpHandler().Stall("/api/version");
        var provider = new OpenAiCompatibleProvider(
            new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(200) },
            new AppSettings { LocalServerUrl = "http://localhost:1234", LocalModel = "m" }
        );

        var status = await provider.ProbeAsync(CancellationToken.None);

        Assert.Equal(ProviderStatusKind.Unreachable, status.Kind);
    }

    [Fact]
    public async Task OpenAi_StreamsOnlyVisibleContentAndReadsUsage()
    {
        var handler = new FakeHttpHandler().Route("/v1/chat/completions", OpenAiChat);
        var deltas = new List<string>();

        await foreach (var delta in Provider(handler, "local-model").StreamAsync(s_request, CancellationToken.None))
        {
            deltas.Add(delta);
        }

        var result = await Provider(handler, "local-model").CompleteAsync(s_request, null, CancellationToken.None);

        Assert.Equal("Hello there", string.Concat(deltas));
        Assert.Equal("Hello there", result.Text);
        Assert.Equal(12, result.InputTokens);
        Assert.Equal(5, result.OutputTokens);
        using var body = JsonDocument.Parse(handler.BodyFor("/v1/chat/completions"));
        Assert.False(body.RootElement.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
        Assert.True(body.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
    }

    [Fact]
    public async Task OpenAi_FailsWhenTheStreamEndsWithoutAFinishOrDone()
    {
        var handler = new FakeHttpHandler().Route(
            "/v1/chat/completions",
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}\n\n"
        );

        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            Provider(handler, "m").CompleteAsync(s_request, null, CancellationToken.None)
        );

        Assert.Equal(ProviderFailureKind.Protocol, ex.Kind);
    }

    [Fact]
    public async Task ServerError_ReportsTheStatusCodeButNotTheBody()
    {
        var handler = new FakeHttpHandler().Route(
            "/v1/chat/completions",
            "echo of the private draft",
            HttpStatusCode.InternalServerError
        );

        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            Provider(handler, "m").CompleteAsync(s_request, null, CancellationToken.None)
        );

        Assert.Equal(ProviderFailureKind.Protocol, ex.Kind);
        Assert.Contains("500", ex.Message);
        Assert.DoesNotContain("draft", ex.Message);
    }

    [Fact]
    public async Task Redirect_IsAFailureAndIsNotFollowed()
    {
        using var elsewhere = new LoopbackHttpServer(() => new LoopbackResponse(HttpStatusCode.OK));
        // ReSharper disable once AccessToDisposedClosure -- server is disposed before elsewhere
        using var server = new LoopbackHttpServer(() =>
            new LoopbackResponse(HttpStatusCode.TemporaryRedirect, elsewhere.Url + "v1/chat/completions")
        );
        using var http = ModelHttpClient.Create(TimeSpan.FromSeconds(30));
        var provider = new OpenAiCompatibleProvider(http, new AppSettings { LocalServerUrl = server.Url, LocalModel = "m" });

        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            provider.CompleteAsync(s_request, null, CancellationToken.None)
        );

        Assert.Contains("307", ex.Message);
        Assert.True(server.Requests > 0);
        Assert.Equal(0, elsewhere.Requests);
    }

    [Fact]
    public async Task Redirect_FollowedByTheClientToAnotherHostIsAFailure()
    {
        using var elsewhere = new LoopbackHttpServer(() => new LoopbackResponse(HttpStatusCode.OK));
        // ReSharper disable once AccessToDisposedClosure -- server is disposed before elsewhere
        using var server = new LoopbackHttpServer(() =>
            new LoopbackResponse(HttpStatusCode.TemporaryRedirect, elsewhere.Url + "api/version")
        );
        using var http = new HttpClient();
        var provider = new OpenAiCompatibleProvider(http, new AppSettings { LocalServerUrl = server.Url, LocalModel = "m" });

        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            provider.CompleteAsync(s_request, null, CancellationToken.None)
        );

        Assert.Equal(ProviderFailureKind.Protocol, ex.Kind);
        Assert.Equal("The local model server redirected the request to another host.", ex.Message);
        Assert.True(elsewhere.Requests > 0);
    }

    [Fact]
    public async Task MissingModel_IsAConfigurationFailure()
    {
        var handler = Ollama().Route("/api/chat", "{\"error\":\"model not found\"}", HttpStatusCode.NotFound);

        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            Provider(handler, "nope").CompleteAsync(s_request, null, CancellationToken.None)
        );

        Assert.Equal(ProviderFailureKind.Configuration, ex.Kind);
    }

    [Fact]
    public async Task ConnectionFailure_IsUnreachable()
    {
        var handler = new FakeHttpHandler().Fail("/api/version", new HttpRequestException("refused"));

        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            Provider(handler, "m").CompleteAsync(s_request, null, CancellationToken.None)
        );
        var status = await Provider(handler, "m").ProbeAsync(CancellationToken.None);

        Assert.Equal(ProviderFailureKind.Unreachable, ex.Kind);
        Assert.Equal(ProviderStatusKind.Unreachable, status.Kind);
    }

    [Fact]
    public async Task Probe_ListsOllamaModelsAndFlagsSmallOnes()
    {
        var handler = Ollama().Route(
            "/api/tags",
            """
            {"models":[
              {"name":"gemma4:26b","details":{"parameter_size":"26.5B","family":"gemma"}},
              {"name":"qwen3:8b","details":{"parameter_size":"8.2B"}},
              {"name":"dictfmt-v2:latest","details":{"parameter_size":"3.1B"}},
              {"name":"mystery"}
            ]}
            """
        );

        var status = await Provider(handler, "gemma4:26b").ProbeAsync(CancellationToken.None);

        Assert.Equal(ProviderStatusKind.Ready, status.Kind);
        Assert.Equal("0.34.1", status.Version);
        Assert.Equal(["gemma4:26b", "qwen3:8b", "dictfmt-v2:latest", "mystery"], status.Models.Select(m => m.Id));
        Assert.Equal([false, true, true, false], status.Models.Select(m => m.IsLowQualityClass));
        Assert.Equal("26.5B", status.Models[0].ParameterSize);
    }

    [Fact]
    public async Task Probe_ListsOpenAiModelsAndReportsAMissingSelection()
    {
        var handler = new FakeHttpHandler().Route("/v1/models", """{"data":[{"id":"a"},{"id":"b"},{"id":"text-embedding-nomic"}]}""");

        var status = await Provider(handler, "c").ProbeAsync(CancellationToken.None);

        Assert.Equal(ProviderStatusKind.NoModel, status.Kind);
        Assert.Equal(["a", "b", "text-embedding-nomic"], status.Models.Select(m => m.Id));
        Assert.Equal([true, true, false], status.Models.Select(m => m.SupportsChat));
        Assert.Null(status.Version);
    }

    [Theory]
    [InlineData("http://localhost:11434", "http://localhost:11434/", "http://localhost:11434/v1/")]
    [InlineData("http://host:1234/v1", "http://host:1234/", "http://host:1234/v1/")]
    [InlineData("http://host:1234/v1/", "http://host:1234/", "http://host:1234/v1/")]
    [InlineData(" https://box/llm/ ", "https://box/llm/", "https://box/llm/v1/")]
    public void NormaliseBaseUrl_AddsTrailingSlashAndV1(string url, string root, string openAi)
    {
        var (actualRoot, actualOpenAi) = OpenAiCompatibleProvider.NormaliseBaseUrl(url);

        Assert.Equal(root, actualRoot.ToString());
        Assert.Equal(openAi, actualOpenAi.ToString());
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://host")]
    [InlineData("")]
    [InlineData(null)]
    public void NormaliseBaseUrl_RejectsInvalidUrls(string? url)
    {
        var ex = Assert.Throws<ProviderException>(() => OpenAiCompatibleProvider.NormaliseBaseUrl(url));

        Assert.Equal(ProviderFailureKind.Configuration, ex.Kind);
    }

    [Theory]
    [InlineData("http://10.0.3.20:11434")]
    [InlineData("http://[fd00::1]")]
    [InlineData("http://ollama.lan:11434")]
    [InlineData("https://models.example.com/")]
    public void NormaliseBaseUrl_AcceptsLocalAddressesAndHostNames(string url)
    {
        OpenAiCompatibleProvider.NormaliseBaseUrl(url);
    }

    [Theory]
    [InlineData("https://203.0.113.10/")]
    [InlineData("http://[2001:db8::1]/")]
    public void NormaliseBaseUrl_RefusesPublicAddressLiterals(string url)
    {
        var ex = Assert.Throws<ProviderException>(() => OpenAiCompatibleProvider.NormaliseBaseUrl(url));

        Assert.Equal(ProviderFailureKind.Configuration, ex.Kind);
        Assert.Equal("The local server address must be on this machine or your local network.", ex.Message);
        Assert.Null(OpenAiCompatibleProvider.IsLoopbackServer(url));
    }

    [Fact]
    public async Task AHostThatResolvesOutsideTheLocalNetwork_ReportsWhy()
    {
        var handler = new FakeHttpHandler().Fail(
            "/api/version",
            new HttpRequestException("wrapped", new OutsideLocalNetworkException())
        );

        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            Provider(handler, "m").CompleteAsync(s_request, null, CancellationToken.None)
        );

        Assert.Equal(ProviderFailureKind.Unreachable, ex.Kind);
        Assert.Equal(new OutsideLocalNetworkException().Message, ex.Message);
    }

    [Theory]
    [InlineData("http://localhost:11434", true)]
    [InlineData("http://127.0.0.1:8080", true)]
    [InlineData("http://[::1]:8080", true)]
    [InlineData("http://10.0.3.20:11434", false)]
    public void RunsLocally_OnlyForLoopback(string url, bool expected)
    {
        var provider = new OpenAiCompatibleProvider(new HttpClient(), new AppSettings { LocalServerUrl = url });

        Assert.Equal(expected, provider.RunsLocally);
    }

    [Theory]
    [InlineData("26.5B", false)]
    [InlineData("12B", false)]
    [InlineData("11.9B", true)]
    [InlineData("500M", true)]
    [InlineData("1.2T", false)]
    [InlineData(null, false)]
    [InlineData("unknown", false)]
    public void IsLowQualityClass_ParsesParameterSizes(string? size, bool expected)
    {
        Assert.Equal(expected, OpenAiCompatibleProvider.IsLowQualityClass(size));
    }

    private const string OllamaTags =
        """
        {"models":[
          {"name":"qwen3.5:27b","details":{"parameter_size":"27.8B"}},
          {"name":"m","details":{"parameter_size":"26B"}},
          {"name":"local-model"},
          {"name":"gpt-oss:120b-cloud","remote_model":"gpt-oss:120b","remote_host":"https://ollama.com","details":{"parameter_size":"116.8B"}},
          {"name":"nomic-embed-text:latest","details":{"parameter_size":"137M"},"capabilities":["embedding"]}
        ]}
        """;

    private static FakeHttpHandler Ollama() =>
        new FakeHttpHandler()
            .Route("/api/version", """{"version":"0.34.1"}""")
            .Route("/api/tags", OllamaTags)
            .RouteByBody(
                "/api/show",
                body => body.Contains("cloud")
                    ? """{"remote_model":"gpt-oss:120b","remote_host":"https://ollama.com","capabilities":["completion"]}"""
                    : body.Contains("embed")
                        ? """{"capabilities":["embedding"]}"""
                        : body.Contains("nope")
                            ? null
                            : """{"capabilities":["completion","tools"]}"""
            );

    private static OpenAiCompatibleProvider Provider(FakeHttpHandler handler, string model) =>
        new(
            new HttpClient(handler),
            new AppSettings { LocalServerUrl = "http://localhost:11434", LocalModel = model }
        );
}
