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
        // ---- customer intents expressed in everyday language -> the wording used in the policy documents ----
        // "discontinue / cancel / close that card" -> the documented action is blocking the card
        (new(@"\b(discontinue|cancel|close|deactivate|terminate|stop|destroy|hotlist)\s+(my\s+|the\s+|that\s+|this\s+|a\s+)?(credit\s+|debit\s+|atm\s+)?card\b", RegexOptions.IgnoreCase), "block card"),
        // "card does not work / stopped working / damaged"
        (new(@"\bcard\s+(?:(?:does|do|did)(?:\s*n[o']?t|nt)?|doe?nt|is\s*n[o']?t|isn'?t|not|stopped|has\s+stopped|won'?t|wont)\s+work(?:ing)?\b", RegexOptions.IgnoreCase), "card block replacement card"),
        (new(@"\b(damaged|broken|faulty|expired|cracked)\s+(credit\s+|debit\s+|atm\s+)?card\b", RegexOptions.IgnoreCase), "block card replacement card"),
        (new(@"\b(card\s+(got\s+|was\s+|is\s+|has\s+been\s+)?(lost|stolen|misplaced)|(lost|stolen|misplaced)\s+(my\s+)?(credit\s+|debit\s+|atm\s+)?card)\b", RegexOptions.IgnoreCase), "lost or stolen card block"),
        // money taken / used without consent -> unauthorised transaction
        (new(@"\b(without\s+my\s+(permission|knowledge|consent|approval)|someone\s+(used|has\s+used|stole|misused)|fraudulent\s+(transaction|charge|debit)|unknown\s+(transaction|charge|debit)|money\s+(was\s+)?(deducted|debited|stolen))\b", RegexOptions.IgnoreCase), "unauthorised transaction report"),
        // paying a loan off early
        (new(@"\b(pay|repay|settle|clear|close)\s+(off\s+)?(my\s+|the\s+)?(home\s+)?loan\s*(early|before|in\s+advance|fully|completely|now)?\b", RegexOptions.IgnoreCase), "loan prepayment foreclosure"),
        (new(@"\bpenalt(y|ies)\b", RegexOptions.IgnoreCase), "charge"),
        (new(@"\bhow\s+much\s+(can|will|could)\s+i\s+(borrow|get)\b", RegexOptions.IgnoreCase), "maximum loan amount"),
        (new(@"\b(not\s+happy|unhappy|dissatisfied|escalate|file\s+a\s+complaint|raise\s+a\s+complaint)\b", RegexOptions.IgnoreCase), "grievance complaint escalation"),
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

    /// <summary>
    /// Light typo tolerance: a word that is not in the visible vocabulary is replaced by the closest vocabulary word
    /// (Damerau-Levenshtein within 1 edit, 2 for long words) when that match is unambiguous. Acronyms and common words are left alone.
    /// </summary>
    public static string Correct(string q, IReadOnlyCollection<string> vocabulary)
    {
        var stems = new HashSet<string>(vocabulary.Select(TextAnalyzer.Stem));
        return Regex.Replace(q, @"[A-Za-z]{4,}", m =>
        {
            var w = m.Value;
            if (w.All(char.IsUpper) || TextAnalyzer.Tokens(w).Count == 0) return w;
            var lw = w.ToLowerInvariant();
            if (stems.Contains(TextAnalyzer.Stem(lw))) return w;
            int max = lw.Length >= 8 ? 2 : 1;
            var cands = vocabulary.Where(v => Math.Abs(v.Length - lw.Length) <= max).Select(v => (v, d: Distance(lw, v))).Where(x => x.d <= max).ToList();
            if (cands.Count == 0) return w;
            int best = cands.Min(c => c.d);
            var top = cands.Where(c => c.d == best).Select(c => TextAnalyzer.Stem(c.v)).Distinct().ToList();
            return top.Count == 1 ? cands.First(c => c.d == best).v : w;
        });
    }

    /// <summary>Optimal string alignment distance (insert, delete, substitute, transpose).</summary>
    public static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        return d[a.Length, b.Length];
    }
}
