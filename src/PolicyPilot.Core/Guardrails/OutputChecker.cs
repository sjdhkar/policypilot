using System.Text.RegularExpressions;
using PolicyPilot.Core.Rag;

namespace PolicyPilot.Core.Guardrails;

public sealed record OutputCheck(bool CitationsValid, double GroundedRatio, IReadOnlyList<int> InvalidCitations, IReadOnlyList<string> UngroundedSentences);

/// <summary>Post-generation checks: every [n] must point at a real source, and each sentence must be supported by retrieved text (token-overlap groundedness).</summary>
public static partial class OutputChecker
{
    [GeneratedRegex(@"\[(\d+)\]")] private static partial Regex CiteRx();

    public static OutputCheck Check(string answer, IReadOnlyList<string> contextTexts, double sentenceThreshold = 0.5)
    {
        var cited = CiteRx().Matches(answer).Select(m => int.Parse(m.Groups[1].Value)).Distinct().ToList();
        var invalid = cited.Where(c => c < 1 || c > contextTexts.Count).ToList();

        var ctx = new HashSet<string>(contextTexts.SelectMany(TextAnalyzer.Tokens));
        var sentences = Regex.Split(CiteRx().Replace(answer, ""), @"(?<=[.!?])\s+|\n+")
            .Select(s => s.Trim()).Where(s => TextAnalyzer.Tokens(s).Count >= 3).ToList();
        var ungrounded = new List<string>();
        foreach (var s in sentences)
        {
            var toks = TextAnalyzer.Tokens(s);
            double overlap = toks.Count(ctx.Contains) / (double)toks.Count;
            if (overlap < sentenceThreshold) ungrounded.Add(s);
        }
        double ratio = sentences.Count == 0 ? 1 : 1 - ungrounded.Count / (double)sentences.Count;
        return new OutputCheck(invalid.Count == 0, ratio, invalid, ungrounded);
    }
}
