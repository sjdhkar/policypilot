namespace PolicyPilot.Core.Rag;

/// <summary>
/// Lightweight second-stage re-ranker (no model download needed). Combines the fused retrieval score with query-term coverage,
/// bigram phrase hits and section-title matches. In production replace with a cross-encoder (bge-reranker / Cohere Rerank / Azure semantic ranker).
/// </summary>
public static class Reranker
{
    public static List<ScoredChunk> Rerank(string query, List<ScoredChunk> pool)
    {
        var q = TextAnalyzer.Tokens(query).Distinct().ToList();
        if (q.Count == 0 || pool.Count == 0) return pool;
        var qBigrams = Bigrams(TextAnalyzer.Tokens(query)).ToHashSet();
        double maxRrf = pool.Max(p => p.Score);

        return pool.Select(p =>
        {
            var toks = TextAnalyzer.Tokens(p.Chunk.IndexText);
            var set = toks.ToHashSet();
            double coverage = q.Count(t => set.Contains(t)) / (double)q.Count;
            double phrase = qBigrams.Count == 0 ? 0 : Bigrams(toks).Count(qBigrams.Contains) / (double)qBigrams.Count;
            var secToks = TextAnalyzer.Tokens(p.Chunk.Section).ToHashSet();
            double secHit = q.Count(secToks.Contains) / (double)q.Count;
            double score = 0.45 * (p.Score / maxRrf) + 0.30 * coverage + 0.15 * Math.Min(1, phrase * 2) + 0.10 * secHit;
            return p with { Score = score };
        }).OrderByDescending(p => p.Score).ToList();
    }

    private static IEnumerable<string> Bigrams(List<string> t)
    {
        for (int i = 0; i + 1 < t.Count; i++) yield return t[i] + " " + t[i + 1];
    }
}
