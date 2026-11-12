namespace PolicyPilot.Core.Rag;

/// <summary>Brute-force cosine similarity over L2-normalised vectors. Fine for thousands of chunks; swap for pgvector / Azure AI Search at scale.</summary>
public sealed class VectorIndex
{
    private readonly List<(Chunk chunk, float[] vec)> _items = new();
    public int Count => _items.Count;
    public void Clear() => _items.Clear();
    public void Add(Chunk c, float[] v) => _items.Add((c, Normalize(v)));

    public static float[] Normalize(float[] v)
    {
        double n = Math.Sqrt(v.Sum(x => (double)x * x));
        if (n == 0) return v;
        return v.Select(x => (float)(x / n)).ToArray();
    }

    public List<(Chunk chunk, double score)> Search(float[] query, int topK, Func<Chunk, bool> allow)
    {
        var q = Normalize(query);
        return _items.Where(i => allow(i.chunk))
            .Select(i => (i.chunk, score: Dot(q, i.vec)))
            .Where(r => r.score > 0)
            .OrderByDescending(r => r.score).Take(topK).ToList();
    }

    private static double Dot(float[] a, float[] b)
    {
        double s = 0; var n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++) s += a[i] * b[i];
        return s;
    }
}
