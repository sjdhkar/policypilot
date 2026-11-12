using System.Security.Cryptography;
using System.Text;
using PolicyPilot.Core.Rag;

namespace PolicyPilot.Core.Llm;

/// <summary>
/// Deterministic, offline embedding using the hashing trick over unigrams + bigrams (TF-weighted, sign-hashed).
/// It is not semantic like a neural model, but it is real vector search that runs anywhere (CI, laptop, air-gapped demo).
/// Swap with OllamaEmbedding / GeminiEmbedding / AzureOpenAI embeddings through configuration.
/// </summary>
public sealed class HashingEmbedding(int dim = 768) : IEmbeddingClient
{
    public string Name => $"hashing-{dim}";

    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var v = new float[dim];
        var toks = TextAnalyzer.Tokens(text);
        void Add(string feature, float w)
        {
            var h = MD5.HashData(Encoding.UTF8.GetBytes(feature));
            int idx = (int)(BitConverter.ToUInt32(h, 0) % (uint)dim);
            float sign = (h[4] & 1) == 0 ? 1f : -1f;
            v[idx] += sign * w;
        }
        foreach (var t in toks) Add(t, 1f);
        for (int i = 0; i + 1 < toks.Count; i++) Add(toks[i] + "_" + toks[i + 1], 0.7f);
        return Task.FromResult(VectorIndex.Normalize(v));
    }
}
