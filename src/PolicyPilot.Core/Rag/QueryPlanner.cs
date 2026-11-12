using System.Text.RegularExpressions;

namespace PolicyPilot.Core.Rag;

/// <summary>
/// Query understanding before retrieval:
///  (1) Normalise: map customer/industry vocabulary to the wording used in the documents (CIBIL -> credit score).
///  (2) Decompose: split a compound question ("X and Y") into sub-questions so each topic is retrieved separately,
///      because one blended query lets the strongest topic crowd out the others.
/// With an LLM, both steps can be done by a rewriting prompt; these rules keep the pipeline deterministic and testable.
/// </summary>
public static partial class QueryPlanner
{
    private static readonly (Regex pattern, string replacement)[] Synonyms =
    [
        (new(@"\bcibil(\s+score)?\b", RegexOptions.IgnoreCase), "credit score"),
        (new(@"\bcredit\s+(rating|report)\b", RegexOptions.IgnoreCase), "credit score"),
        (new(@"\bcredit\s+score\s+score\b", RegexOptions.IgnoreCase), "credit score"),
        (new(@"\btenor\b", RegexOptions.IgnoreCase), "tenure"),
        (new(@"\broi\b", RegexOptions.IgnoreCase), "interest rate"),
        (new(@"\bpre-?closure\b", RegexOptions.IgnoreCase), "foreclosure"),
        (new(@"\bpre-?payment\s+penalty\b", RegexOptions.IgnoreCase), "prepayment charge"),
        (new(@"\bdisputed?\s+charge(s)?\b", RegexOptions.IgnoreCase), "unauthorised transaction"),
    ];

    [GeneratedRegex(@"\s*(?:,|;|&|\band\b|\balso\b|\bplus\b|\bas well as\b)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex Splitter();

    public static string Normalise(string q)
    {
        foreach (var (p, r) in Synonyms) q = p.Replace(q, r);
        return q.Trim();
    }

    /// <summary>Returns the sub-questions (one element when the question is not compound).</summary>
    public static IReadOnlyList<string> Decompose(string q)
    {
        var parts = Splitter().Split(q).Select(p => p.Trim(' ', '?', '.')).Where(p => TextAnalyzer.Tokens(p).Count >= 1).ToList();
        if (parts.Count < 2 || parts.Count > 4) return [q];
        return parts;
    }
}
