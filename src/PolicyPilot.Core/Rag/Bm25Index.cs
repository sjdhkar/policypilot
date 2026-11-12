namespace PolicyPilot.Core.Rag;

/// <summary>In-memory Okapi BM25 over chunk text. Access control is applied at query time via the allow predicate.</summary>
public sealed class Bm25Index(double k1 = 1.5, double b = 0.75)
{
    private readonly List<(Chunk chunk, Dictionary<string, int> tf, int len)> _docs = new();
    private readonly Dictionary<string, int> _df = new();
    private double _avgLen;

    public int Count => _docs.Count;

    /// <summary>True if the (already stemmed) term occurs in any chunk the caller is allowed to see.</summary>
    public bool Contains(string term, Func<Chunk, bool> allow) => _docs.Any(d => d.tf.ContainsKey(term) && allow(d.chunk));

    public void Build(IEnumerable<Chunk> chunks)
    {
        _docs.Clear(); _df.Clear();
        foreach (var c in chunks)
        {
            var toks = TextAnalyzer.Tokens(c.IndexText);
            var tf = new Dictionary<string, int>();
            foreach (var t in toks) tf[t] = tf.GetValueOrDefault(t) + 1;
            foreach (var t in tf.Keys) _df[t] = _df.GetValueOrDefault(t) + 1;
            _docs.Add((c, tf, toks.Count));
        }
        _avgLen = _docs.Count == 0 ? 0 : _docs.Average(d => d.len);
    }

    public List<(Chunk chunk, double score)> Search(string query, int topK, Func<Chunk, bool> allow)
    {
        var q = TextAnalyzer.Tokens(query).Distinct().ToList();
        var n = _docs.Count;
        var res = new List<(Chunk, double)>();
        foreach (var (chunk, tf, len) in _docs)
        {
            if (!allow(chunk)) continue;
            double s = 0;
            foreach (var t in q)
            {
                if (!tf.TryGetValue(t, out var f)) continue;
                var df = _df[t];
                var idf = Math.Log(1 + (n - df + 0.5) / (df + 0.5));
                s += idf * (f * (k1 + 1)) / (f + k1 * (1 - b + b * len / Math.Max(1, _avgLen)));
            }
            if (s > 0) res.Add((chunk, s));
        }
        return res.OrderByDescending(r => r.Item2).Take(topK).ToList();
    }
}
