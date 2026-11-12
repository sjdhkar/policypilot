using PolicyPilot.Core;
using PolicyPilot.Core.Llm;
static class Debug
{
    public static async Task Show(string docs, string role, string q)
    {
        var app = await PolicyPilotApp.CreateAsync("mock", new ProviderOptions(), docs);
        var a = await app.Rag.AnswerAsync(q, new UserContext("d", role));
        Console.WriteLine($"[{role}] {q}\n  answered={a.Answered} conf={a.Confidence} flags={string.Join(",", a.Flags)}\n  {a.Answer}\n  cites={string.Join(" | ", a.Citations.Select(c => c.DocId + ">" + c.Section))}");
    }
}
static class Dump
{
    public static async Task Chunks(string docs, string docId)
    {
        var app = await PolicyPilot.Core.PolicyPilotApp.CreateAsync("mock", new PolicyPilot.Core.Llm.ProviderOptions(), docs);
        foreach (var c in app.Kb.Chunks.Where(c => c.DocId == docId)) Console.WriteLine($"{c.Id} | {c.Section} | {c.Text.Replace("\n", "⏎")}");
    }
}
static class ProviderCheck
{
    /// <summary>Exercises complete/stream/embed of every HTTP provider against a base URL (use tools/fake_llm_server.py).</summary>
    public static async Task<int> Run(string baseUrl)
    {
        int bad = 0;
        void Chk(string n, bool ok) { Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {n}"); if (!ok) bad++; }
        var req = new PolicyPilot.Core.LlmRequest([new("user", "hi")]);
        var o = new PolicyPilot.Core.Llm.ProviderOptions { BaseUrl = baseUrl, ApiKey = "k", ChatModel = "m", EmbeddingModel = "e" };
        foreach (var (name, c) in new (string, object)[] { ("ollama", new PolicyPilot.Core.Llm.OllamaClient(o)), ("gemini", new PolicyPilot.Core.Llm.GeminiClient(o)), ("azure", new PolicyPilot.Core.Llm.AzureOpenAiClient(o)) })
        {
            var llm = (PolicyPilot.Core.ILlmClient)c; var emb = (PolicyPilot.Core.IEmbeddingClient)c;
            var r = await llm.CompleteAsync(req);
            Chk($"{name} complete -> '{r.Text}' tokens {r.PromptTokens}/{r.CompletionTokens}", r.Text == $"Hello from {name}" && r.PromptTokens > 0);
            var sb = new System.Text.StringBuilder(); await foreach (var t in llm.StreamAsync(req)) sb.Append(t);
            Chk($"{name} stream -> '{sb}'", sb.ToString() == $"Hello from {name}");
            var v = await emb.EmbedAsync("x");
            Chk($"{name} embed dim {v.Length}", v.Length == 4);
        }
        var badKey = new PolicyPilot.Core.Llm.AzureOpenAiClient(o with { ApiKey = "wrong" });
        try { await badKey.CompleteAsync(req); Chk("azure bad key raises", false); } catch (HttpRequestException) { Chk("azure bad key raises HttpRequestException", true); }
        return bad;
    }
}
