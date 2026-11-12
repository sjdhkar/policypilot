using System.Diagnostics;
using PolicyPilot.Core.Guardrails;
using PolicyPilot.Core.Observability;

namespace PolicyPilot.Core.Rag;

public sealed record RagOptions
{
    public double MinTopCoverage { get; init; } = 0.34;     // share of question terms that must appear in the best chunk
    public double MaxUnknownTermRatio { get; init; } = 0.5;  // share of query terms absent from the user's visible corpus
    public double MinGroundedRatio { get; init; } = 0.6;    // share of answer sentences supported by context
    public RetrievalOptions Retrieval { get; init; } = new();
}

/// <summary>
/// The full RAG flow: input guardrails -> query contextualisation -> hybrid retrieval (ACL-filtered) -> confidence gate
/// -> prompt build (with indirect-injection filtering) -> generation -> output guardrails -> cited answer with a stage trace.
/// </summary>
public sealed class RagPipeline(KnowledgeBase kb, HybridRetriever retriever, ILlmClient llm, Telemetry telemetry, RagOptions? options = null)
{
    private readonly RagOptions _o = options ?? new RagOptions();
    public const string NoAnswer = "I could not find this in the policy documents I can access.";

    public async Task<RagAnswer> AnswerAsync(string question, UserContext user, IReadOnlyList<ChatTurn>? history = null, CancellationToken ct = default)
    {
        history ??= [];
        var timings = new List<StageTiming>();
        var flags = new List<string>();
        var sw = Stopwatch.StartNew();
        using var activity = telemetry.Source.StartActivity("rag.answer");

        double Lap(string stage) { var ms = sw.Elapsed.TotalMilliseconds; timings.Add(new(stage, Math.Round(ms, 2))); sw.Restart(); return ms; }

        // 1) Input guardrails
        var (inj, _) = InjectionDetector.Check(question);
        if (inj)
        {
            telemetry.GuardrailTrips.Add(1, new KeyValuePair<string, object?>("type", "prompt_injection"));
            return new RagAnswer("I can't help with that request. Please ask a question about the bank's policies.", [], 0, false, ["prompt_injection_blocked"], timings);
        }
        var (safeQuestion, pii) = PiiRedactor.Redact(question);
        if (pii.Count > 0) { flags.Add("pii_redacted:" + string.Join(",", pii)); telemetry.GuardrailTrips.Add(1, new KeyValuePair<string, object?>("type", "pii")); }
        Lap("input_guardrails");

        // 2) Contextualise follow-ups ("what about prepayment?") using the last user turn
        var searchQuery = Contextualise(safeQuestion, history);
        Lap("contextualise");

        // 3) Retrieve (ACL applied inside)
        var hits = await retriever.RetrieveAsync(searchQuery, user, _o.Retrieval, ct);
        Lap("retrieve");

        // 4) Indirect prompt injection: strip only the offending lines from retrieved chunks (keep the legitimate text)
        var clean = new List<ScoredChunk>();
        foreach (var h in hits)
        {
            if (!InjectionDetector.Check(h.Chunk.Text).Detected) { clean.Add(h); continue; }
            flags.Add("indirect_injection_sanitised");
            var kept = System.Text.RegularExpressions.Regex.Split(h.Chunk.Text, @"(?<=[.!?])\s+|\n").Where(l => !InjectionDetector.Check(l).Detected).ToList();
            var text = string.Join(" ", kept).Trim();
            if (text.Length > 0) clean.Add(h with { Chunk = h.Chunk with { Text = text } });
        }

        // 5) Confidence gate: (a) the best chunk must cover enough query terms, and
        //    (b) query terms that do not exist anywhere in documents THIS user may see signal an unanswerable question
        //    (this also stops a customer getting a wrong answer about an officer-only topic).
        var qTerms = TextAnalyzer.Tokens(searchQuery).Distinct().ToList();
        Func<Chunk, bool> visible = c => c.AllowedRoles.Contains(user.Role, StringComparer.OrdinalIgnoreCase);
        double unknown = qTerms.Count == 0 ? 1 : qTerms.Count(t => !kb.Bm25.Contains(t, visible)) / (double)qTerms.Count;
        // An unknown ACRONYM (FOIR, LTV, NPA...) is the subject of the question, so answering from neighbouring text would be wrong.
        bool unknownAcronym = System.Text.RegularExpressions.Regex.Matches(safeQuestion, @"\b[A-Z]{3,6}\b")
            .Any(m => TextAnalyzer.Tokens(m.Value) is { Count: > 0 } t && !kb.Bm25.Contains(t[0], visible));
        // A term that exists in the corpus but ONLY in documents this user cannot see marks a restricted topic (e.g. "fraud desk" for a customer).
        bool restricted = qTerms.Any(t => !kb.Bm25.Contains(t, visible) && kb.Bm25.Contains(t, _ => true));
        bool unknownGate = unknown >= _o.MaxUnknownTermRatio || unknownAcronym || restricted;
        double coverage = clean.Count == 0 ? 0 : Coverage(searchQuery, clean[0].Chunk.IndexText);
        double confidence = clean.Count == 0 ? 0 : Math.Round(Math.Min(1, 0.6 * coverage + 0.4 * clean[0].Score), 3);
        if (clean.Count == 0 || coverage < _o.MinTopCoverage || unknownGate)
        {
            telemetry.NoAnswer.Add(1);
            Lap("confidence_gate");
            return new RagAnswer(NoAnswer, [], confidence, false, flags.Append(unknownGate ? $"unknown_terms:ratio={unknown:0.00},acr={unknownAcronym},restricted={string.Join("/", qTerms.Where(t => !kb.Bm25.Contains(t, visible) && kb.Bm25.Contains(t, _ => true)))}" : "low_retrieval_confidence").ToList(), timings);
        }
        Lap("confidence_gate");

        // 6) Generate
        var messages = PromptBuilder.Build(safeQuestion, clean, history);
        var result = await llm.CompleteAsync(new LlmRequest(messages), ct);
        telemetry.RecordLlm(llm.Name, result.PromptTokens, result.CompletionTokens);
        Lap("generate");

        // 7) Output guardrails
        var answer = PiiRedactor.Redact(result.Text).Text;
        var check = OutputChecker.Check(answer, clean.Select(c => c.Chunk.Text).ToList());
        if (!check.CitationsValid) { flags.Add("invalid_citation"); answer = System.Text.RegularExpressions.Regex.Replace(answer, @"\[(\d+)\]", m => check.InvalidCitations.Contains(int.Parse(m.Groups[1].Value)) ? "" : m.Value); }
        bool refused = answer.Contains("could not find", StringComparison.OrdinalIgnoreCase);
        if (!refused && check.GroundedRatio < _o.MinGroundedRatio)
        {
            flags.Add($"low_groundedness:{check.GroundedRatio:0.00}");
            telemetry.NoAnswer.Add(1);
            Lap("output_guardrails");
            return new RagAnswer(NoAnswer, [], confidence, false, flags, timings);
        }
        Lap("output_guardrails");

        var citations = clean.Select((c, i) => new Citation(i + 1, c.Chunk.DocId, c.Chunk.DocTitle, c.Chunk.Section, Snip(c.Chunk.Text), Math.Round(c.Score, 3))).ToList();
        telemetry.Answered.Add(1);
        return new RagAnswer(answer.Trim(), citations, confidence, !refused, flags, timings);
    }

    public static string Contextualise(string question, IReadOnlyList<ChatTurn> history)
    {
        var prev = history.LastOrDefault(h => h.Role == "user")?.Content;
        if (prev is null) return question;
        var toks = TextAnalyzer.Tokens(question);
        bool followUp = toks.Count <= 3 || System.Text.RegularExpressions.Regex.IsMatch(question, @"\b(it|that|this|those|they|them|its|same)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return followUp ? $"{prev} {question}" : question;
    }

    private static double Coverage(string query, string text)
    {
        var q = TextAnalyzer.Tokens(query).Distinct().ToList();
        if (q.Count == 0) return 0;
        var set = TextAnalyzer.Tokens(text).ToHashSet();
        return q.Count(set.Contains) / (double)q.Count;
    }

    private static string Snip(string t) => t.Length <= 220 ? t : t[..220] + "…";
}
