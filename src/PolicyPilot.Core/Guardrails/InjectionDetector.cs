using System.Text.RegularExpressions;

namespace PolicyPilot.Core.Guardrails;

/// <summary>
/// Heuristic prompt-injection / jailbreak detector for user input and for retrieved text (indirect injection).
/// Heuristics are a first line of defence; production adds a classifier (Azure Prompt Shields / Llama Guard) and strict tool permissions.
/// </summary>
public static class InjectionDetector
{
    private static readonly Regex[] Patterns =
    [
        new(@"ignore\s+(all\s+|any\s+|the\s+)?(previous|prior|above|earlier)\s+(instructions|rules|prompts?)", RegexOptions.IgnoreCase),
        new(@"disregard\s+(all\s+|any\s+|the\s+)?(previous|prior|above|earlier|your)\s+(instructions|rules|guidelines)", RegexOptions.IgnoreCase),
        new(@"(reveal|show|print|repeat|leak)\s+(me\s+)?(your|the)\s+(system|hidden|initial)\s+(prompt|instructions|message)", RegexOptions.IgnoreCase),
        new(@"you\s+are\s+now\s+(a|an|the)?\s*\w+", RegexOptions.IgnoreCase),
        new(@"\b(jailbreak|developer\s+mode|DAN\s+mode|do\s+anything\s+now)\b", RegexOptions.IgnoreCase),
        new(@"pretend\s+(to\s+be|you\s+are)", RegexOptions.IgnoreCase),
        new(@"(bypass|override|disable)\s+(the\s+)?(safety|guardrails?|filters?|restrictions?|access\s+control)", RegexOptions.IgnoreCase),
        new(@"act\s+as\s+(an?\s+)?(admin|administrator|root|officer|system)", RegexOptions.IgnoreCase),
        new(@"</?\s*(system|assistant)\s*>", RegexOptions.IgnoreCase),
    ];

    public static (bool Detected, string? Pattern) Check(string text)
    {
        foreach (var p in Patterns)
            if (p.IsMatch(text)) return (true, p.ToString());
        return (false, null);
    }
}
