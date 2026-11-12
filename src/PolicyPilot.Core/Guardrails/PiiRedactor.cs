using System.Text.RegularExpressions;

namespace PolicyPilot.Core.Guardrails;

/// <summary>Masks Indian banking PII (PAN, Aadhaar, mobile, email, card numbers) before text reaches an LLM, logs or traces.</summary>
public static partial class PiiRedactor
{
    [GeneratedRegex(@"\b[A-Z]{5}[0-9]{4}[A-Z]\b")] private static partial Regex Pan();
    [GeneratedRegex(@"\b[2-9][0-9]{3}\s?[0-9]{4}\s?[0-9]{4}\b")] private static partial Regex Aadhaar();
    [GeneratedRegex(@"(?<!\d)(?:\+91[\s-]?)?[6-9][0-9]{9}(?!\d)")] private static partial Regex Mobile();
    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")] private static partial Regex Email();
    [GeneratedRegex(@"\b(?:\d[ -]?){13,19}\b")] private static partial Regex Card();

    public static (string Text, List<string> Found) Redact(string input)
    {
        var found = new List<string>();
        var s = input;
        s = Card().Replace(s, m =>
        {
            var digits = new string(m.Value.Where(char.IsDigit).ToArray());
            if (digits.Length is < 13 or > 19 || !Luhn(digits)) return m.Value;
            found.Add("CARD"); return "[CARD]";
        });
        s = Replace(s, Email(), "[EMAIL]", "EMAIL", found);
        s = Replace(s, Pan(), "[PAN]", "PAN", found);
        s = Replace(s, Aadhaar(), "[AADHAAR]", "AADHAAR", found);
        s = Replace(s, Mobile(), "[PHONE]", "PHONE", found);
        return (s, found.Distinct().ToList());
    }

    private static string Replace(string s, Regex r, string token, string label, List<string> found)
    {
        if (!r.IsMatch(s)) return s;
        found.Add(label);
        return r.Replace(s, token);
    }

    public static bool Luhn(string digits)
    {
        int sum = 0; bool alt = false;
        for (int i = digits.Length - 1; i >= 0; i--)
        {
            int n = digits[i] - '0';
            if (alt) { n *= 2; if (n > 9) n -= 9; }
            sum += n; alt = !alt;
        }
        return sum % 10 == 0;
    }
}
