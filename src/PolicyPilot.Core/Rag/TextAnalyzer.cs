using System.Text.RegularExpressions;

namespace PolicyPilot.Core.Rag;

/// <summary>Lowercasing tokenizer with stop-word removal and a light suffix stemmer (shared by BM25, hashing embeddings, reranker and eval).</summary>
public static partial class TextAnalyzer
{
    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "a","an","the","and","or","of","to","in","on","for","with","is","are","was","were","be","been","by","at","as","it","its",
        "this","that","these","those","from","can","do","does","did","i","my","me","we","our","you","your","what","which","who",
        "how","when","where","will","would","should","could","may","if","than","then","there","their","them","has","have","had",
        "not","no","any","per","about","into","also","so","such","tell","please"
    };
    // generic question filler words (kept out of the index and the query so they cannot trigger the restricted-term gate)
    private static readonly string[] Filler = ["many","much","long","take","takes","handle","handles","within","should","would","could","need","needs","get","give","tell","please","whom","does","happen","happens"];

    [GeneratedRegex("[a-z0-9]+(?:\\.[0-9]+)?", RegexOptions.IgnoreCase)]
    private static partial Regex WordRegex();

    public static List<string> Tokens(string text)
    {
        var res = new List<string>();
        foreach (Match m in WordRegex().Matches(text.ToLowerInvariant()))
        {
            var w = m.Value;
            if (w.Length < 2 || Stop.Contains(w) || Array.IndexOf(Filler, w) >= 0) continue;
            res.Add(Stem(w));
        }
        return res;
    }

    public static string Stem(string w)
    {
        if (w.Length <= 3 || char.IsDigit(w[0])) return w;
        foreach (var suf in new[] { "ations", "ation", "ingly", "ings", "ing", "edly", "ies", "ied", "ed", "es", "s" })
            if (w.EndsWith(suf) && w.Length - suf.Length >= 3)
                return suf == "ies" || suf == "ied" ? w[..^suf.Length] + "y" : w[..^suf.Length];
        return w;
    }

    public static int WordCount(string s) => s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}
