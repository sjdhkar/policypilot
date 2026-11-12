using System.Text.Json;
using PolicyPilot.Core.Rag;

namespace PolicyPilot.Core.Agent;

public sealed record ToolSpec(string Name, string Description, string ArgsSchema);

public interface ITool
{
    ToolSpec Spec { get; }
    Task<string> RunAsync(JsonElement args, UserContext user, CancellationToken ct);
}

/// <summary>Deterministic maths belongs in code, not in the LLM: the model decides WHEN to call it, code computes the result.</summary>
public sealed class EmiCalculatorTool : ITool
{
    public ToolSpec Spec => new("emi_calculator", "Compute monthly EMI for a loan.", "{principal:number, annualRatePct:number, tenureMonths:number}");

    public static double Emi(double principal, double annualRatePct, double months)
    {
        if (principal <= 0 || months <= 0) throw new ArgumentException("principal and tenure must be positive");
        var r = annualRatePct / 12 / 100;
        if (r == 0) return principal / months;
        var p = Math.Pow(1 + r, months);
        return principal * r * p / (p - 1);
    }

    public Task<string> RunAsync(JsonElement a, UserContext user, CancellationToken ct)
    {
        var emi = Emi(a.GetProperty("principal").GetDouble(), a.GetProperty("annualRatePct").GetDouble(), a.GetProperty("tenureMonths").GetDouble());
        var total = emi * a.GetProperty("tenureMonths").GetDouble();
        return Task.FromResult($"EMI is {emi:N0} per month; total repayment {total:N0}; total interest {(total - a.GetProperty("principal").GetDouble()):N0}.");
    }
}

/// <summary>Implements the fictional FOIR rule from the sample credit policy (max 50% of income to obligations, min score 700).</summary>
public sealed class LoanEligibilityTool : ITool
{
    public const double MaxFoir = 0.50;
    public const double MinScore = 700;
    public const double IndicativeRatePct = 10.5;

    public ToolSpec Spec => new("loan_eligibility", "Indicative home-loan eligibility check using the bank's FOIR and credit score rules.",
        "{monthlyIncome:number, existingEmi:number, requestedAmount:number, tenureMonths:number, creditScore:number}");

    public Task<string> RunAsync(JsonElement a, UserContext user, CancellationToken ct)
    {
        double income = a.GetProperty("monthlyIncome").GetDouble(), existing = a.GetProperty("existingEmi").GetDouble();
        double amount = a.GetProperty("requestedAmount").GetDouble(), tenure = a.GetProperty("tenureMonths").GetDouble();
        double score = a.TryGetProperty("creditScore", out var cs) ? cs.GetDouble() : 750;
        var newEmi = EmiCalculatorTool.Emi(amount, IndicativeRatePct, tenure);
        var foir = (existing + newEmi) / income;
        var reasons = new List<string>();
        if (score < MinScore) reasons.Add($"credit score {score:0} is below the minimum {MinScore:0}");
        if (foir > MaxFoir) reasons.Add($"FOIR {foir:P0} exceeds the {MaxFoir:P0} limit");
        var ok = reasons.Count == 0;
        return Task.FromResult(ok
            ? $"Indicatively ELIGIBLE. New EMI {newEmi:N0} at {IndicativeRatePct}% gives FOIR {foir:P0} (limit {MaxFoir:P0}), credit score {score:0} meets the minimum."
            : $"Indicatively NOT eligible: {string.Join("; ", reasons)}. New EMI would be {newEmi:N0}.");
    }
}

/// <summary>Lets the agent reuse the RAG retriever; ACL still applies because the caller's role is passed through.</summary>
public sealed class PolicySearchTool(HybridRetriever retriever) : ITool
{
    public ToolSpec Spec => new("policy_search", "Search bank policy documents the user is allowed to see.", "{query:string}");

    public async Task<string> RunAsync(JsonElement a, UserContext user, CancellationToken ct)
    {
        var hits = await retriever.RetrieveAsync(a.GetProperty("query").GetString() ?? "", user, new RetrievalOptions(FinalK: 3), ct);
        if (hits.Count == 0) return "No relevant policy found.";
        return string.Join(" | ", hits.Select(h => $"({h.Chunk.DocTitle} > {h.Chunk.Section}) {h.Chunk.Text}"));
    }
}
