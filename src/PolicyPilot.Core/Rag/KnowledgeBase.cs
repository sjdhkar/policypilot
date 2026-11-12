namespace PolicyPilot.Core.Rag;

/// <summary>
/// Owns documents, chunks, embeddings and both indexes (BM25 + vector). Indexes are rebuilt in place after each write,
/// which is instant at this scale. Writes take a lock; the API serialises ingest calls.
/// </summary>
public sealed class KnowledgeBase(IEmbeddingClient embedder, Chunker chunker)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DocumentMeta> _docs = new();
    private readonly Dictionary<string, (Chunk chunk, float[] vec)> _items = new();

    public Bm25Index Bm25 { get; } = new();
    public VectorIndex Vectors { get; } = new();
    public IEmbeddingClient Embedder => embedder;

    /// <summary>Lower-case words (4+ letters) from the chunks a role may read; used for typo correction.</summary>
    public IReadOnlyCollection<string> VocabularyFor(string role)
    {
        lock (_gate)
            return _items.Values.Where(i => i.chunk.AllowedRoles.Contains(role, StringComparer.OrdinalIgnoreCase))
                .SelectMany(i => System.Text.RegularExpressions.Regex.Matches(i.chunk.IndexText.ToLowerInvariant(), "[a-z]{4,}").Select(m => m.Value))
                .ToHashSet();
    }

    public IReadOnlyList<DocumentMeta> Documents { get { lock (_gate) return _docs.Values.OrderBy(d => d.Title).ToList(); } }
    public int ChunkCount { get { lock (_gate) return _items.Count; } }
    public IReadOnlyList<Chunk> Chunks { get { lock (_gate) return _items.Values.Select(i => i.chunk).OrderBy(c => c.Id).ToList(); } }

    public async Task<int> IngestAsync(DocumentMeta meta, string text, CancellationToken ct = default)
    {
        var chunks = chunker.Split(meta, text);
        var embedded = new List<(Chunk, float[])>();
        foreach (var c in chunks) embedded.Add((c, await embedder.EmbedAsync(c.IndexText, ct)));

        lock (_gate)
        {
            DropInternal(meta.Id);
            _docs[meta.Id] = meta;
            foreach (var (c, v) in embedded) _items[c.Id] = (c, v);
            Rebuild();
        }
        return chunks.Count;
    }

    public bool Remove(string docId)
    {
        lock (_gate)
        {
            var existed = DropInternal(docId);
            if (existed) Rebuild();
            return existed;
        }
    }

    private bool DropInternal(string docId)
    {
        if (!_docs.Remove(docId)) return false;
        foreach (var key in _items.Where(kv => kv.Value.chunk.DocId == docId).Select(kv => kv.Key).ToList()) _items.Remove(key);
        return true;
    }

    private void Rebuild()
    {
        Bm25.Build(_items.Values.Select(i => i.chunk));
        Vectors.Clear();
        foreach (var (c, v) in _items.Values) Vectors.Add(c, v);
    }

    /// <summary>Loads every *.md file in a folder. Optional front matter: id, title, category, roles (comma separated).</summary>
    public async Task<int> LoadFolderAsync(string folder, CancellationToken ct = default)
    {
        if (!Directory.Exists(folder)) return 0;
        int total = 0;
        foreach (var file in Directory.EnumerateFiles(folder, "*.md").OrderBy(f => f))
        {
            var (meta, body) = ParseFrontMatter(Path.GetFileNameWithoutExtension(file), await File.ReadAllTextAsync(file, ct), Path.GetFileName(file));
            total += await IngestAsync(meta, body, ct);
        }
        return total;
    }

    public static (DocumentMeta, string) ParseFrontMatter(string defaultId, string text, string source)
    {
        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var body = text.Replace("\r\n", "\n");
        if (body.StartsWith("---\n"))
        {
            var end = body.IndexOf("\n---", 4, StringComparison.Ordinal);
            if (end > 0)
            {
                foreach (var line in body[4..end].Split('\n'))
                {
                    var i = line.IndexOf(':');
                    if (i > 0) props[line[..i].Trim()] = line[(i + 1)..].Trim();
                }
                body = body[(end + 4)..].TrimStart('\n');
            }
        }
        var roles = props.GetValueOrDefault("roles", "customer,officer,admin").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var id = props.GetValueOrDefault("id", defaultId);
        return (new DocumentMeta(id, props.GetValueOrDefault("title", id), props.GetValueOrDefault("category", "general"), roles, source), body);
    }

    /// <summary>Extracts plain text from a .docx (zip of XML) using only the framework, so uploads work without extra packages.</summary>
    public static string ExtractDocx(Stream docx)
    {
        using var zip = new System.IO.Compression.ZipArchive(docx, System.IO.Compression.ZipArchiveMode.Read);
        var entry = zip.GetEntry("word/document.xml") ?? throw new InvalidDataException("Not a .docx file");
        using var s = entry.Open();
        var xdoc = System.Xml.Linq.XDocument.Load(s);
        System.Xml.Linq.XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var sb = new System.Text.StringBuilder();
        foreach (var p in xdoc.Descendants(w + "p"))
        {
            var style = p.Element(w + "pPr")?.Element(w + "pStyle")?.Attribute(w + "val")?.Value ?? "";
            var text = string.Concat(p.Descendants(w + "t").Select(t => t.Value));
            if (string.IsNullOrWhiteSpace(text)) { sb.AppendLine(); continue; }
            if (style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase)) sb.AppendLine("## " + text);
            else sb.AppendLine(text);
        }
        return sb.ToString();
    }
}
