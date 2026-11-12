namespace PolicyPilot.Core;

/// <summary>Metadata for an ingested document. AllowedRoles drives document-level access control.</summary>
public record DocumentMeta(string Id, string Title, string Category, string[] AllowedRoles, string Source);

/// <summary>A retrievable unit of text with its provenance.</summary>
public record Chunk(string Id, string DocId, string DocTitle, string Section, string Text, string[] AllowedRoles, int Ordinal)
{
    /// <summary>Text used for lexical/vector indexing: title + section make short chunks self-describing.</summary>
    public string IndexText => $"{DocTitle}. {Section}. {Text}";
}

public record ScoredChunk(Chunk Chunk, double Score, double Bm25Score, double VectorScore, int? Bm25Rank, int? VectorRank);

public record UserContext(string UserId, string Role)
{
    public static UserContext Anonymous => new("anonymous", "customer");
}

public record Citation(int Index, string DocId, string DocTitle, string Section, string Snippet, double Score);

public record ChatTurn(string Role, string Content);

public record StageTiming(string Stage, double Ms);

public record RagAnswer(
    string Answer,
    IReadOnlyList<Citation> Citations,
    double Confidence,
    bool Answered,
    IReadOnlyList<string> Flags,
    IReadOnlyList<StageTiming> Timings,
    string? ToolTrace = null);
