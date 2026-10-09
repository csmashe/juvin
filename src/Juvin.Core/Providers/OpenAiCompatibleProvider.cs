using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Juvin.Core.Models;
using Juvin.Core.Providers.Streaming;

namespace Juvin.Core.Providers;

/// <summary>
///     Local mode: an OpenAI-compatible server such as Ollama, LM Studio, or llama.cpp. Ollama is
///     detected and driven through its native chat API, which honours <c>think: false</c>.
/// </summary>
public sealed partial class OpenAiCompatibleProvider : IModelProvider
{
    private const double LowQualityBelowBillions = 12;

    // Deliberately low (English runs near four): text with many symbols, identifiers, or non-Latin
    // characters tokenizes denser, and a window that is too small loses context silently.
    private const int CharactersPerToken = 2;
    private const int OutputTokenReserve = 2048;
    private const int MinContextTokens = 8192;
    private const int MaxContextTokens = 32768;

    private readonly HttpClient _http;
    private readonly string? _model;
    private readonly Uri _openAiBase;
    private readonly Uri _root;

    private bool? _isOllama;
    private string? _ollamaVersion;

    /// <exception cref="ProviderException">The server URL is not a valid http or https URL.</exception>
    public OpenAiCompatibleProvider(HttpClient http, AppSettings settings)
    {
        _http = http;
        (_root, _openAiBase) = NormaliseBaseUrl(settings.LocalServerUrl);
        _model = string.IsNullOrWhiteSpace(settings.LocalModel) ? null : settings.LocalModel.Trim();
        RunsLocally = _root.IsLoopback;
    }

    public ProcessingMode Mode => ProcessingMode.Local;

    public string DisplayName => "Local model";

    public bool SupportsStreaming => true;

    public bool RunsLocally { get; }

    public async Task<ProviderStatus> ProbeAsync(CancellationToken ct)
    {
        using var deadline = Deadline(ct);
        try
        {
            var models = await IsOllamaAsync(deadline.Token)
                ? await ListOllamaModelsAsync(deadline.Token)
                : await ListOpenAiModelsAsync(deadline.Token);
            var selected = _model is null ? null : models.FirstOrDefault(m => ModelMatches(m.Id, _model));
            return selected switch
            {
                null => new ProviderStatus(ProviderStatusKind.NoModel, "Provider.Status.NoModel", models, _ollamaVersion),
                { IsRemote: true } => new ProviderStatus(ProviderStatusKind.NoModel, "Provider.Status.RemoteModel", models, _ollamaVersion),
                { SupportsChat: false } => new ProviderStatus(ProviderStatusKind.NoModel, "Provider.Status.NotAChatModel", models, _ollamaVersion),
                _ => new ProviderStatus(ProviderStatusKind.Ready, "Provider.Status.Ready", models, _ollamaVersion),
            };
        }
        catch (ProviderException ex) when (ex.Kind is ProviderFailureKind.Unreachable or ProviderFailureKind.Protocol)
        {
            Trace.WriteLine($"[OpenAiCompatibleProvider] Probe failed: {ex.Kind}");
            return new ProviderStatus(
                ProviderStatusKind.Unreachable,
                ex.Kind == ProviderFailureKind.Unreachable
                    ? "Provider.Status.Unreachable"
                    : "Provider.Status.BadResponse",
                []
            );
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ProviderStatus(ProviderStatusKind.Unreachable, "Provider.Status.Unreachable", []);
        }
    }

    public async IAsyncEnumerable<string> StreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        using var deadline = Deadline(ct);
        await using var deltas = StreamCoreAsync(request, new StreamUsage(), deadline.Token).GetAsyncEnumerator(deadline.Token);
        while (true)
        {
            bool more;
            try
            {
                more = await deltas.MoveNextAsync();
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw TimedOut(ex);
            }

            if (!more)
            {
                yield break;
            }

            yield return deltas.Current;
        }
    }

    public async Task<ChatResult> CompleteAsync(
        ChatRequest request,
        IProgress<string>? preview,
        CancellationToken ct
    )
    {
        var model = ResolveModel(request);
        var stopwatch = Stopwatch.StartNew();
        var usage = new StreamUsage();
        var pump = new StreamPump(text => preview?.Report(text));
        string text;
        using (var deadline = Deadline(ct))
        {
            try
            {
                text = (await pump.RunAsync(StreamCoreAsync(request, usage, deadline.Token), deadline.Token)).TrimEnd();
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw TimedOut(ex);
            }
        }

        Trace.WriteLine(
            $"[OpenAiCompatibleProvider] Completed: model={model}, chars={text.Length}, ms={stopwatch.ElapsedMilliseconds}"
        );
        return new ChatResult(text, usage.Model ?? model, usage.InputTokens, usage.OutputTokens);
    }

    internal static (Uri Root, Uri OpenAiBase) NormaliseBaseUrl(string url)
    {
        if (
            !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
        )
        {
            throw new ProviderException(
                ProviderFailureKind.Configuration,
                "The local server address is not a valid http or https URL."
            );
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^3];
        }

        var root = new UriBuilder(uri) { Path = path + "/", Query = "", Fragment = "" }.Uri;
        return (root, new Uri(root, "v1/"));
    }

    internal static bool IsLowQualityClass(string? parameterSize)
    {
        var match = ParameterSizePattern().Match(parameterSize ?? "");
        if (!match.Success)
        {
            return false;
        }

        var value = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var billions = char.ToUpperInvariant(match.Groups[2].Value[0]) switch
        {
            'K' => value / 1_000_000,
            'M' => value / 1_000,
            'T' => value * 1_000,
            _ => value,
        };
        return billions < LowQualityBelowBillions;
    }

    /// <summary>Ollama lists capabilities per model; one without "completion" (an embedding model) cannot chat.</summary>
    private static bool SupportsChat(JsonElement model) =>
        !model.TryGetProperty("capabilities", out var capabilities)
        || capabilities.ValueKind != JsonValueKind.Array
        || capabilities.EnumerateArray().Any(c => c.ValueKind == JsonValueKind.String && c.GetString() == "completion");

    private static bool ModelMatches(string id, string configured) =>
        string.Equals(id, configured, StringComparison.OrdinalIgnoreCase)
        || string.Equals(id, configured + ":latest", StringComparison.OrdinalIgnoreCase);

    private string ResolveModel(ChatRequest request) =>
        request.Model
        ?? _model
        ?? throw new ProviderException(
            ProviderFailureKind.Configuration,
            "No local model is selected."
        );

    /// <summary>
    ///     <see cref="HttpClient.Timeout" /> only covers the headers once a response is streamed, so
    ///     the same budget is applied to the whole exchange through this token.
    /// </summary>
    private CancellationTokenSource Deadline(CancellationToken ct)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (_http.Timeout != Timeout.InfiniteTimeSpan)
        {
            source.CancelAfter(_http.Timeout);
        }

        return source;
    }

    private static ProviderException TimedOut(Exception ex) =>
        new(ProviderFailureKind.Unreachable, "The local model server did not respond in time.", ex);

    private async IAsyncEnumerable<string> StreamCoreAsync(
        ChatRequest request,
        StreamUsage usage,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        var model = ResolveModel(request);
        var isOllama = await IsOllamaAsync(ct);
        if (isOllama)
        {
            await CheckOllamaModelAsync(model, ct);
        }

        var deltas = isOllama
            ? ReadOllamaChatAsync(request, model, usage, ct)
            : ReadOpenAiChatAsync(request, model, usage, ct);

        var filter = new ThinkingBlockStreamFilter();
        await foreach (var delta in deltas.WithCancellation(ct))
        {
            var visible = filter.Push(delta);
            if (visible.Length > 0)
            {
                yield return visible;
            }
        }

        var rest = filter.Flush();
        if (rest.Length > 0)
        {
            yield return rest;
        }
    }

    private async IAsyncEnumerable<string> ReadOllamaChatAsync(
        ChatRequest request,
        string model,
        StreamUsage usage,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = BuildMessages(request),
            ["stream"] = true,
            ["options"] = new JsonObject
            {
                ["temperature"] = request.Temperature,
                ["num_ctx"] = ContextWindowFor(request),
            },
        };
        if (request.DisableThinking)
        {
            body["think"] = false;
        }

        using var response = await PostChatAsync(new Uri(_root, "api/chat"), body, model, ct);
        using var reader = new StreamReader(await ReadStreamAsync(response, ct));
        while (await ReadLineAsync(reader, ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var chunk = ParseOllamaChunk(line, usage);
            if (chunk.Content.Length > 0)
            {
                yield return chunk.Content;
            }

            // ReSharper disable once InvertIf -- the end of the stream reads better as the positive case
            if (chunk.Done)
            {
                ThrowIfCutOff(chunk.DoneReason);
                yield break;
            }
        }

        throw IncompleteReply();
    }

    private async IAsyncEnumerable<string> ReadOpenAiChatAsync(
        ChatRequest request,
        string model,
        StreamUsage usage,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = BuildMessages(request),
            ["stream"] = true,
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
            ["temperature"] = request.Temperature,
        };
        if (request.DisableThinking)
        {
            // The llama.cpp and LM Studio switch; other servers ignore it.
            body["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false };
        }

        using var response = await PostChatAsync(new Uri(_openAiBase, "chat/completions"), body, model, ct);
        using var reader = new StreamReader(await ReadStreamAsync(response, ct));
        var finished = false;
        await foreach (var sseEvent in SseEventDecoder.DecodeAsync(reader, ct))
        {
            if (sseEvent.Data == "[DONE]")
            {
                yield break;
            }

            var chunk = ParseOpenAiChunk(sseEvent.Data, usage);
            if (chunk.Content.Length > 0)
            {
                yield return chunk.Content;
            }

            // ReSharper disable once InvertIf -- the end of the stream reads better as the positive case
            if (chunk.FinishReason is not null)
            {
                ThrowIfCutOff(chunk.FinishReason);
                finished = true;
            }
        }

        // Some servers close the stream after the finish reason without sending [DONE].
        if (!finished)
        {
            throw IncompleteReply();
        }
    }

    /// <summary>
    ///     Ollama silently drops the oldest messages, and with them the draft and thread, when a
    ///     conversation outgrows its context window. The window is sized to the conversation in
    ///     steps, so a growing follow-up chat rarely forces a model reload; one too long for the
    ///     largest step is refused.
    /// </summary>
    internal static int ContextWindowFor(ChatRequest request)
    {
        var characters = request.SystemPrompt.Length + request.Messages.Sum(message => message.Content.Length);
        var needed = characters / CharactersPerToken + OutputTokenReserve;
        var window = MinContextTokens;
        while (window < needed)
        {
            window *= 2;
            if (window > MaxContextTokens)
            {
                throw new ProviderException(
                    ProviderFailureKind.Configuration,
                    "The conversation is too long for the local model. Start a new rewrite."
                );
            }
        }

        return window;
    }

    private static JsonArray BuildMessages(ChatRequest request)
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = request.SystemPrompt },
        };
        foreach (var message in request.Messages)
        {
            messages.Add(
                new JsonObject
                {
                    ["role"] = message.Role == ChatRole.User ? "user" : "assistant",
                    ["content"] = message.Content,
                }
            );
        }

        return messages;
    }

    private static (string Content, bool Done, string? DoneReason) ParseOllamaChunk(string line, StreamUsage usage)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            ThrowIfServerError(root);
            usage.Model = GetString(root, "model") ?? usage.Model;
            var done = root.TryGetProperty("done", out var doneElement) && doneElement.ValueKind == JsonValueKind.True;
            if (done)
            {
                usage.InputTokens = GetInt(root, "prompt_eval_count");
                usage.OutputTokens = GetInt(root, "eval_count");
            }

            var content = root.TryGetProperty("message", out var message)
                ? GetString(message, "content") ?? ""
                : "";
            return (content, done, done ? GetString(root, "done_reason") : null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw UnreadableReply(ex);
        }
    }

    /// <summary>A reply the server cut short would silently lose deadlines or commitments.</summary>
    private static void ThrowIfCutOff(string? reason)
    {
        switch (reason?.ToLowerInvariant())
        {
            case "length":
                throw new ProviderException(
                    ProviderFailureKind.Protocol,
                    "The model hit its output limit before finishing the reply."
                );
            case "content_filter":
                throw new ProviderException(
                    ProviderFailureKind.Protocol,
                    "The model server's content filter stopped the reply."
                );
        }
    }

    private static (string Content, string? FinishReason) ParseOpenAiChunk(string data, StreamUsage usage)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            ThrowIfServerError(root);
            usage.Model = GetString(root, "model") ?? usage.Model;
            if (root.TryGetProperty("usage", out var tokens) && tokens.ValueKind == JsonValueKind.Object)
            {
                usage.InputTokens = GetInt(tokens, "prompt_tokens");
                usage.OutputTokens = GetInt(tokens, "completion_tokens");
            }

            if (
                !root.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0
            )
            {
                return ("", null);
            }

            var choice = choices[0];
            var content = choice.TryGetProperty("delta", out var delta)
                ? GetString(delta, "content") ?? ""
                : "";
            return (content, GetString(choice, "finish_reason"));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw UnreadableReply(ex);
        }
    }

    private static void ThrowIfServerError(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Expected a JSON object.");
        }

        if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
        {
            throw new ProviderException(
                ProviderFailureKind.Protocol,
                "The local model server reported an error while answering."
            );
        }
    }

    private async Task<bool> IsOllamaAsync(CancellationToken ct)
    {
        if (_isOllama is { } known)
        {
            return known;
        }

        using var response = await SendAsync(
            new HttpRequestMessage(HttpMethod.Get, new Uri(_root, "api/version")),
            ct
        );
        if (response.IsSuccessStatusCode)
        {
            try
            {
                using var document = JsonDocument.Parse(await ReadStringAsync(response, ct));
                _ollamaVersion = document.RootElement.ValueKind == JsonValueKind.Object
                    ? GetString(document.RootElement, "version")
                    : null;
            }
            catch (JsonException)
            {
                _ollamaVersion = null;
            }
        }

        _isOllama = _ollamaVersion is not null;
        Trace.WriteLine($"[OpenAiCompatibleProvider] Server detected: ollama={_isOllama}");
        return _isOllama.Value;
    }

    /// <summary>
    ///     Asks Ollama about the requested model so aliases such as <c>library/name</c> resolve the
    ///     way the chat call will. A cloud model is refused: Local mode never sends a draft off the
    ///     server, whatever its address. Checked every turn because a tag can be repointed.
    /// </summary>
    private async Task CheckOllamaModelAsync(string model, CancellationToken ct)
    {
        var shown = await ShowOllamaModelAsync(model, ct);
        if (shown is null)
        {
            throw new ProviderException(
                ProviderFailureKind.Configuration,
                $"The local model server has no model named '{model}'."
            );
        }

        if (shown.Value.Remote)
        {
            throw new ProviderException(
                ProviderFailureKind.Configuration,
                $"The model '{model}' runs in Ollama's cloud, not on the server. Local mode only uses models that run on the server."
            );
        }

        if (!shown.Value.SupportsChat)
        {
            throw new ProviderException(
                ProviderFailureKind.Configuration,
                $"The model '{model}' only makes embeddings and cannot write text."
            );
        }
    }

    /// <summary>Null when the server has no such model.</summary>
    private async Task<(bool Remote, bool SupportsChat)?> ShowOllamaModelAsync(string model, CancellationToken ct)
    {
        using var response = await SendAsync(
            new HttpRequestMessage(HttpMethod.Post, new Uri(_root, "api/show"))
            {
                Content = new StringContent(
                    new JsonObject { ["model"] = model }.ToJsonString(),
                    Encoding.UTF8,
                    "application/json"
                ),
            },
            ct
        );
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        ThrowIfFailed(response, model: null);
        try
        {
            using var document = JsonDocument.Parse(await ReadStringAsync(response, ct));
            var root = document.RootElement;
            // ReSharper disable once ConvertIfStatementToReturnStatement -- a throw inside a conditional return is harder to read
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("Expected a JSON object.");
            }

            return (IsRemote(root), SupportsChat(root));
        }
        catch (JsonException ex)
        {
            throw UnreadableReply(ex);
        }
    }

    private static bool IsRemote(JsonElement model) =>
        !string.IsNullOrEmpty(GetString(model, "remote_host"))
        || !string.IsNullOrEmpty(GetString(model, "remote_model"));

    private async Task<IReadOnlyList<ModelInfo>> ListOllamaModelsAsync(CancellationToken ct)
    {
        List<(ModelInfo Info, bool NeedsShow)> listed;
        using (var document = await GetJsonAsync(new Uri(_root, "api/tags"), ct))
        {
            try
            {
                listed =
                [
                    .. document
                        .RootElement.GetProperty("models")
                        .EnumerateArray()
                        .Select(model =>
                        {
                            var name = GetString(model, "name") ?? throw new JsonException("Model without a name.");
                            var size = model.TryGetProperty("details", out var details)
                                ? GetString(details, "parameter_size")
                                : null;
                            var info = new ModelInfo(name, name, size, IsLowQualityClass(size), IsRemote(model), SupportsChat(model));
                            // Older Ollama versions list no capabilities; the show endpoint has them.
                            return (info, !model.TryGetProperty("capabilities", out _));
                        }),
                ];
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
            {
                throw UnreadableReply(ex);
            }
        }

        var models = new List<ModelInfo>(listed.Count);
        foreach (var (info, needsShow) in listed)
        {
            var shown = needsShow ? await ShowOllamaModelAsync(info.Id, ct) : null;
            models.Add(
                shown is null
                    ? info
                    : info with { IsRemote = info.IsRemote || shown.Value.Remote, SupportsChat = shown.Value.SupportsChat }
            );
        }

        return models;
    }

    private async Task<IReadOnlyList<ModelInfo>> ListOpenAiModelsAsync(CancellationToken ct)
    {
        using var document = await GetJsonAsync(new Uri(_openAiBase, "models"), ct);
        try
        {
            return
            [
                .. document
                    .RootElement.GetProperty("data")
                    .EnumerateArray()
                    .Select(model =>
                    {
                        var id = GetString(model, "id") ?? throw new JsonException("Model without an id.");
                        // The OpenAI model list carries no capabilities; the name is the only clue.
                        var embedding = id.Contains("embed", StringComparison.OrdinalIgnoreCase);
                        return new ModelInfo(id, id, null, false, SupportsChat: !embedding);
                    }),
            ];
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw UnreadableReply(ex);
        }
    }

    private async Task<JsonDocument> GetJsonAsync(Uri uri, CancellationToken ct)
    {
        using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, uri), ct);
        ThrowIfFailed(response, model: null);
        try
        {
            return JsonDocument.Parse(await ReadStringAsync(response, ct));
        }
        catch (JsonException ex)
        {
            throw UnreadableReply(ex);
        }
    }

    private async Task<HttpResponseMessage> PostChatAsync(Uri uri, JsonObject body, string model, CancellationToken ct)
    {
        var response = await SendAsync(
            new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            },
            ct
        );
        ThrowIfFailed(response, model);
        return response;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderException(
                ProviderFailureKind.Unreachable,
                $"Could not reach the local model server at {_root.Authority}.",
                ex
            );
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ProviderException(
                ProviderFailureKind.Unreachable,
                "The local model server did not respond in time.",
                ex
            );
        }
        finally
        {
            request.Dispose();
        }
    }

    /// <summary>Reports the status code only; an error body could echo the prompt.</summary>
    private static void ThrowIfFailed(HttpResponseMessage response, string? model)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var status = response.StatusCode;
        response.Dispose();
        Trace.WriteLine($"[OpenAiCompatibleProvider] HTTP {(int)status}");
        throw status == HttpStatusCode.NotFound && model is not null
            ? new ProviderException(
                ProviderFailureKind.Configuration,
                $"The local model server has no model named '{model}'."
            )
            : new ProviderException(
                ProviderFailureKind.Protocol,
                $"The local model server answered with HTTP {(int)status}."
            );
    }

    private static async Task<Stream> ReadStreamAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStreamAsync(ct);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            throw SseEventDecoder.MapBodyReadFailure(ex, ct);
        }
    }

    private static async Task<string> ReadStringAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            throw SseEventDecoder.MapBodyReadFailure(ex, ct);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw SseEventDecoder.MapBodyReadFailure(ex, ct);
        }
    }

    private static async Task<string?> ReadLineAsync(TextReader reader, CancellationToken ct)
    {
        try
        {
            return await reader.ReadLineAsync(ct);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            throw SseEventDecoder.MapBodyReadFailure(ex, ct);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw SseEventDecoder.MapBodyReadFailure(ex, ct);
        }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : null;

    private static ProviderException IncompleteReply() =>
        new(ProviderFailureKind.Protocol, "The local model server ended the reply early.");

    private static ProviderException UnreadableReply(Exception ex) =>
        new(ProviderFailureKind.Protocol, "The local model server sent a reply Juvin could not read.", ex);

    [GeneratedRegex(@"^\s*(\d+(?:\.\d+)?)\s*([KMBT])", RegexOptions.IgnoreCase)]
    private static partial Regex ParameterSizePattern();

    private sealed class StreamUsage
    {
        public string? Model { get; set; }

        public int? InputTokens { get; set; }

        public int? OutputTokens { get; set; }
    }
}
