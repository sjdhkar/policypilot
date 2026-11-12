namespace PolicyPilot.Core.Rag;

public sealed record RetrievalOptions(int CandidatesPerRetriever = 20, int RerankPool = 8, int FinalK = 4, int RrfK = 60);

/// <summary>
/// Hybrid retrieval: BM25 (exact terms, numbers, product names) + dense vectors (paraphrase) fused with Reciprocal Rank Fusion,
/// then re-ranked with cheap cross-features. ACL filtering happens BEFORE scoring so restricted text can never leak.
/// </summary>
public sealed class HybridRetriever(Bm25Index bm25, VectorIndex vectors, IEmbeddingClient embedder)
{
    public async Task<List<ScoredChunk>> RetrieveAsync(string query, UserContext user, RetrievalOptions? opt = null, CancellationToken ct = default)
    {
        opt ??= new RetrievalOptions();
        bool Allow(Chunk c) => c.AllowedRoles.Contains(user.Role, StringComparer.OrdinalIgnoreCase);

        var lex = bm25.Search(query, opt.CandidatesPerRetriever, Allow);
        var qvec = await embedder.EmbedAsync(query, ct);
        var dense = vectors.Search(qvec, opt.CandidatesPerRetriever, Allow);

        var fused = new Dictionary<string, (Chunk c, double rrf, double bm, double vec, int? bmRank, int? vRank)>();
        for (int i = 0; i < lex.Count; i++)
        {
            var (c, s) = lex[i];
            fused[c.Id] = (c, 1.0 / (opt.RrfK + i + 1), s, 0, i + 1, null);
        }
        for (int i = 0; i < dense.Count; i++)
        {
            var (c, s) = dense[i];
            fused.TryGetValue(c.Id, out var e);
            fused[c.Id] = (c, (e.c is null ? 0 : e.rrf) + 1.0 / (opt.RrfK + i + 1), e.c is null ? 0 : e.bm, s, e.c is null ? null : e.bmRank, i + 1);
        }

        var pool = fused.Values.OrderByDescending(x => x.rrf).Take(opt.RerankPool)
            .Select(x => new ScoredChunk(x.c, x.rrf, x.bm, x.vec, x.bmRank, x.vRank)).ToList();

        return Reranker.Rerank(query, pool).Take(opt.FinalK).ToList();
    }
}
