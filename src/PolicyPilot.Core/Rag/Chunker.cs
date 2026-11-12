using System.Text;

namespace PolicyPilot.Core.Rag;

/// <summary>
/// Heading-aware chunking. Splits markdown on headings first (so a chunk never crosses a topic boundary),
/// then packs paragraphs into windows of ~MaxWords with a small word overlap so facts at a boundary stay retrievable.
/// </summary>
public sealed class Chunker(int maxWords = 110, int overlapWords = 20)
{
    public List<Chunk> Split(DocumentMeta doc, string text)
    {
        var chunks = new List<Chunk>();
        var section = doc.Title;
        var buf = new StringBuilder();
        int ord = 0;

        void Flush(string sec, string body)
        {
            var words = body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return;
            for (int start = 0; start < words.Length; start += Math.Max(1, maxWords - overlapWords))
            {
                var take = words.Skip(start).Take(maxWords).ToArray();
                chunks.Add(new Chunk($"{doc.Id}#{ord:D3}", doc.Id, doc.Title, sec, string.Join(' ', take), doc.AllowedRoles, ord));
                ord++;
                if (start + maxWords >= words.Length) break;
            }
        }

        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith('#'))
            {
                Flush(section, buf.ToString()); buf.Clear();
                section = line.TrimStart('#', ' ').Trim();
            }
            else buf.AppendLine(line);
        }
        Flush(section, buf.ToString());
        return chunks;
    }
}
