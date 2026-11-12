using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using PolicyPilot.Core.Rag;

namespace PolicyPilot.Core.Llm;

/// <summary>
/// Offline "LLM" used for CI, demos and evaluation determinism. It does two things:
/// (1) RAG answers by EXTRACTION: picks the context sentences that best match the question and cites them (so groundedness is guaranteed);
/// (2) agent planning: a tiny rule-based planner that emits the same JSON tool-call protocol a real model would.
/// Replace with Ollama/Gemini/Azure OpenAI via config; the pipeline does not change.
/// </summary>
public sealed partial class MockLlm : ILlmClient
{
    public string Name => "mock-extractive";

    [GeneratedRegex(@"^\[(\d+)\]\s*\(([^)]*)\)\s*(.*)$")] private static partial Regex CtxLine();
    [GeneratedRegex(@"-?\d+(?:,\d{3})*(?:\.\d+)?")] private static partial Regex Num();

    public Task<LlmResult> CompleteAsync(LlmRequest r, CancellationToken ct = default)
    {
        var sys = r.Messages.First().Content;
        var text = sys.Contains("TOOL-USING AGENT") ? Plan(r) : Extract(r);
        return Task.FromResult(new LlmResult(text, TokenEstimator.Estimate(string.Join(" ", r.Messages.Select(m => m.Content))), TokenEstimator.Estimate(text)));
    }

    public async IAsyncEnumerable<string> StreamAsync(LlmRequest r, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var res = await CompleteAsync(r, ct);
        foreach (var w in Regex.Split(res.Text, @"(?<=\s)"))
        {
            if (w.Length == 0) continue;
            await Task.Delay(8, ct);
            yield return w;
        }
    }

    private static string Extract(LlmRequest r)
    {
        var user = r.Messages.Last().Content;
        var q = Regex.Match(user, @"QUESTION:\s*(.*?)\s*ANSWER", RegexOptions.Singleline).Groups[1].Value;
        var qt = TextAnalyzer.Tokens(q).Distinct().ToList();
        var ctxBlock = Regex.Match(user, @"CONTEXT:\s*(.*?)\s*QUESTION:", RegexOptions.Singleline).Groups[1].Value;

        var cand = new List<(int idx, string sentence, double score)>();
        foreach (var line in ctxBlock.Split('\n'))
        {
            var m = CtxLine().Match(line.Trim());
            if (!m.Success) continue;
            int idx = int.Parse(m.Groups[1].Value);
            foreach (var s in Regex.Split(m.Groups[3].Value, @"(?<=[.!?])\s+"))
            {
                var st = TextAnalyzer.Tokens(s);
                if (st.Count < 3) continue;
                var set = st.ToHashSet(); set.UnionWith(TextAnalyzer.Tokens(m.Groups[2].Value.Split('>').Last()));
                double score = qt.Count(set.Contains) / (double)Math.Max(1, qt.Count);
                cand.Add((idx, s.Trim(), score));
            }
        }
        var best = cand.Where(c => c.score >= 0.2).OrderByDescending(c => c.score).ThenBy(c => c.idx).Take(3).ToList();
        if (best.Count == 0) return "I could not find this in the policy documents.";
        return string.Join(" ", best.OrderBy(b => b.idx).Select(b => $"{b.sentence.TrimEnd()} [{b.idx}]"));
    }

    private static string Plan(LlmRequest r)
    {
        var question = r.Messages.First(m => m.Role == "user").Content;
        var toolResults = r.Messages.Where(m => m.Content.StartsWith("TOOL_RESULT")).ToList();
        if (toolResults.Count > 0)
        {
            var last = toolResults.Last().Content;
            return JsonSerializer.Serialize(new { final = "Result: " + last[(last.IndexOf(':') + 1)..].Trim() });
        }
        var lower = question.ToLowerInvariant();
        var nums = Num().Matches(question).Select(m => double.Parse(m.Value.Replace(",", ""))).ToList();
        if (lower.Contains("eligib") && nums.Count >= 3)
            return JsonSerializer.Serialize(new
            {
                tool = "loan_eligibility",
                args = new { monthlyIncome = nums[0], existingEmi = nums[1], requestedAmount = nums[2], tenureMonths = nums.Count > 3 ? nums[3] : 240, creditScore = nums.Count > 4 ? nums[4] : 750 }
            });
        if (lower.Contains("emi") && nums.Count >= 3)
            return JsonSerializer.Serialize(new { tool = "emi_calculator", args = new { principal = nums[0], annualRatePct = nums[1], tenureMonths = nums[2] } });
        return JsonSerializer.Serialize(new { tool = "policy_search", args = new { query = question } });
    }
}
