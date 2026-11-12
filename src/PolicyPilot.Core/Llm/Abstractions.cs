namespace PolicyPilot.Core;

public sealed record LlmMessage(string Role, string Content);
public sealed record LlmRequest(IReadOnlyList<LlmMessage> Messages, double Temperature = 0.1, int MaxTokens = 600);
public sealed record LlmResult(string Text, int PromptTokens, int CompletionTokens);

public interface ILlmClient
{
    string Name { get; }
    Task<LlmResult> CompleteAsync(LlmRequest request, CancellationToken ct = default);
    IAsyncEnumerable<string> StreamAsync(LlmRequest request, CancellationToken ct = default);
}

public interface IEmbeddingClient
{
    string Name { get; }
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);
}

public static class TokenEstimator
{
    /// <summary>Rough token estimate (~4 chars/token) used for cost and latency dashboards when the provider does not report usage.</summary>
    public static int Estimate(string s) => Math.Max(1, s.Length / 4);
}
