using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PolicyPilot.Core.Llm;

public sealed record ProviderOptions
{
    public string BaseUrl { get; init; } = "";
    public string ApiKey { get; init; } = "";
    public string ChatModel { get; init; } = "";
    public string EmbeddingModel { get; init; } = "";
    public string ApiVersion { get; init; } = "2024-06-01";
    public int TimeoutSeconds { get; init; } = 120;
}

internal static class Http
{
    public static HttpClient Create(ProviderOptions o) => new() { Timeout = TimeSpan.FromSeconds(o.TimeoutSeconds) };

    public static async Task<JsonNode> PostJson(HttpClient c, string url, object body, Action<HttpRequestMessage>? configure, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        configure?.Invoke(req);
        using var resp = await c.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"LLM provider error {(int)resp.StatusCode}: {text}");
        return JsonNode.Parse(text)!;
    }
}

/// <summary>Local, free: Ollama (llama3.x, qwen, mistral...) via its REST API.</summary>
public sealed class OllamaClient(ProviderOptions o) : ILlmClient, IEmbeddingClient
{
    private readonly HttpClient _http = Http.Create(o);
    public string Name => $"ollama:{o.ChatModel}";

    public async Task<LlmResult> CompleteAsync(LlmRequest r, CancellationToken ct = default)
    {
        var body = new { model = o.ChatModel, stream = false, messages = r.Messages.Select(m => new { role = m.Role, content = m.Content }),
                         options = new { temperature = r.Temperature, num_predict = r.MaxTokens } };
        var j = await Http.PostJson(_http, $"{o.BaseUrl.TrimEnd('/')}/api/chat", body, null, ct);
        var text = j["message"]?["content"]?.GetValue<string>() ?? "";
        return new LlmResult(text, j["prompt_eval_count"]?.GetValue<int>() ?? 0, j["eval_count"]?.GetValue<int>() ?? 0);
    }

    public async IAsyncEnumerable<string> StreamAsync(LlmRequest r, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var body = new { model = o.ChatModel, stream = true, messages = r.Messages.Select(m => new { role = m.Role, content = m.Content }),
                         options = new { temperature = r.Temperature, num_predict = r.MaxTokens } };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{o.BaseUrl.TrimEnd('/')}/api/chat") { Content = JsonContent.Create(body) };
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(ct));
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var j = JsonNode.Parse(line);
            var piece = j?["message"]?["content"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(piece)) yield return piece;
            if (j?["done"]?.GetValue<bool>() == true) yield break;
        }
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var j = await Http.PostJson(_http, $"{o.BaseUrl.TrimEnd('/')}/api/embeddings", new { model = o.EmbeddingModel, prompt = text }, null, ct);
        return j["embedding"]!.AsArray().Select(x => x!.GetValue<float>()).ToArray();
    }
}

/// <summary>Google Gemini free tier via the Generative Language REST API.</summary>
public sealed class GeminiClient(ProviderOptions o) : ILlmClient, IEmbeddingClient
{
    private readonly HttpClient _http = Http.Create(o);
    public string Name => $"gemini:{o.ChatModel}";
    private string Base => string.IsNullOrEmpty(o.BaseUrl) ? "https://generativelanguage.googleapis.com" : o.BaseUrl.TrimEnd('/');

    private object BuildBody(LlmRequest r)
    {
        var system = string.Join("\n", r.Messages.Where(m => m.Role == "system").Select(m => m.Content));
        var contents = r.Messages.Where(m => m.Role != "system")
            .Select(m => new { role = m.Role == "assistant" ? "model" : "user", parts = new[] { new { text = m.Content } } });
        return new { systemInstruction = new { parts = new[] { new { text = system } } }, contents,
                     generationConfig = new { temperature = r.Temperature, maxOutputTokens = r.MaxTokens } };
    }

    private static string Extract(JsonNode? j) =>
        string.Concat(j?["candidates"]?[0]?["content"]?["parts"]?.AsArray().Select(p => p?["text"]?.GetValue<string>() ?? "") ?? Enumerable.Empty<string>());

    public async Task<LlmResult> CompleteAsync(LlmRequest r, CancellationToken ct = default)
    {
        var j = await Http.PostJson(_http, $"{Base}/v1beta/models/{o.ChatModel}:generateContent", BuildBody(r),
            q => q.Headers.Add("x-goog-api-key", o.ApiKey), ct);
        return new LlmResult(Extract(j), j["usageMetadata"]?["promptTokenCount"]?.GetValue<int>() ?? 0,
            j["usageMetadata"]?["candidatesTokenCount"]?.GetValue<int>() ?? 0);
    }

    public async IAsyncEnumerable<string> StreamAsync(LlmRequest r, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{Base}/v1beta/models/{o.ChatModel}:streamGenerateContent?alt=sse")
        { Content = JsonContent.Create(BuildBody(r)) };
        req.Headers.Add("x-goog-api-key", o.ApiKey);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(ct));
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (!line.StartsWith("data:")) continue;
            var piece = Extract(JsonNode.Parse(line[5..].Trim()));
            if (!string.IsNullOrEmpty(piece)) yield return piece;
        }
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var body = new { model = $"models/{o.EmbeddingModel}", content = new { parts = new[] { new { text } } } };
        var j = await Http.PostJson(_http, $"{Base}/v1beta/models/{o.EmbeddingModel}:embedContent", body,
            q => q.Headers.Add("x-goog-api-key", o.ApiKey), ct);
        return j["embedding"]!["values"]!.AsArray().Select(x => x!.GetValue<float>()).ToArray();
    }
}

/// <summary>Azure OpenAI (enterprise path). BaseUrl = https://{resource}.openai.azure.com, ChatModel/EmbeddingModel = deployment names.</summary>
public sealed class AzureOpenAiClient(ProviderOptions o) : ILlmClient, IEmbeddingClient
{
    private readonly HttpClient _http = Http.Create(o);
    public string Name => $"azure-openai:{o.ChatModel}";

    private string ChatUrl => $"{o.BaseUrl.TrimEnd('/')}/openai/deployments/{o.ChatModel}/chat/completions?api-version={o.ApiVersion}";

    private object Body(LlmRequest r, bool stream) => new
    {
        messages = r.Messages.Select(m => new { role = m.Role, content = m.Content }),
        temperature = r.Temperature, max_tokens = r.MaxTokens, stream
    };

    public async Task<LlmResult> CompleteAsync(LlmRequest r, CancellationToken ct = default)
    {
        var j = await Http.PostJson(_http, ChatUrl, Body(r, false), q => q.Headers.Add("api-key", o.ApiKey), ct);
        return new LlmResult(j["choices"]![0]!["message"]!["content"]!.GetValue<string>(),
            j["usage"]?["prompt_tokens"]?.GetValue<int>() ?? 0, j["usage"]?["completion_tokens"]?.GetValue<int>() ?? 0);
    }

    public async IAsyncEnumerable<string> StreamAsync(LlmRequest r, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, ChatUrl) { Content = JsonContent.Create(Body(r, true)) };
        req.Headers.Add("api-key", o.ApiKey);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(ct));
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (!line.StartsWith("data:")) continue;
            var data = line[5..].Trim();
            if (data == "[DONE]") yield break;
            var piece = JsonNode.Parse(data)?["choices"]?[0]?["delta"]?["content"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(piece)) yield return piece;
        }
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var url = $"{o.BaseUrl.TrimEnd('/')}/openai/deployments/{o.EmbeddingModel}/embeddings?api-version={o.ApiVersion}";
        var j = await Http.PostJson(_http, url, new { input = text }, q => q.Headers.Add("api-key", o.ApiKey), ct);
        return j["data"]![0]!["embedding"]!.AsArray().Select(x => x!.GetValue<float>()).ToArray();
    }
}
