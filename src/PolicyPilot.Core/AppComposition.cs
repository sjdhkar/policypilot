using PolicyPilot.Core.Agent;
using PolicyPilot.Core.Llm;
using PolicyPilot.Core.Observability;
using PolicyPilot.Core.Rag;

namespace PolicyPilot.Core;

/// <summary>Composition root shared by the API and the evaluation runner, so both exercise exactly the same wiring.</summary>
public sealed class PolicyPilotApp
{
    public required KnowledgeBase Kb { get; init; }
    public required RagPipeline Rag { get; init; }
    public required AgentRunner Agent { get; init; }
    public required Telemetry Telemetry { get; init; }
    public required ILlmClient Llm { get; init; }
    public required HybridRetriever Retriever { get; init; }

    /// <summary>provider: mock | ollama | gemini | azure</summary>
    public static async Task<PolicyPilotApp> CreateAsync(string provider, ProviderOptions opts, string docsFolder, CancellationToken ct = default)
    {
        ILlmClient llm; IEmbeddingClient emb;
        switch (provider.ToLowerInvariant())
        {
            case "ollama": { var c = new OllamaClient(opts); llm = c; emb = c; break; }
            case "gemini": { var c = new GeminiClient(opts); llm = c; emb = c; break; }
            case "azure": { var c = new AzureOpenAiClient(opts); llm = c; emb = c; break; }
            default: llm = new MockLlm(); emb = new HashingEmbedding(); break;
        }
        var kb = new KnowledgeBase(emb, new Chunker());
        await kb.LoadFolderAsync(docsFolder, ct);
        var retriever = new HybridRetriever(kb.Bm25, kb.Vectors, emb);
        var telemetry = new Telemetry();
        var rag = new RagPipeline(kb, retriever, llm, telemetry);
        var agent = new AgentRunner(llm, [new PolicySearchTool(retriever), new EmiCalculatorTool(), new LoanEligibilityTool()]);
        return new PolicyPilotApp { Kb = kb, Rag = rag, Agent = agent, Telemetry = telemetry, Llm = llm, Retriever = retriever };
    }
}
