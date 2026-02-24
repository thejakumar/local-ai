namespace LocalAI.Api.Models;

// ── Retrieval Pipeline Models ───────────────────────────

public record RetrievalOptions(
    string SearchMode = "hybrid",
    int TopK = 5,
    bool EnableQueryExpansion = true,
    bool EnableHyde = true,
    bool EnableReranking = true,
    string? FileTypeFilter = null,
    string? FileNameFilter = null,
    DateTime? DateAfter = null,
    DateTime? DateBefore = null
);

public record RetrievedChunk(
    Guid DocumentId,
    string FileName,
    string Content,
    string FileType,
    int ChunkIndex,
    double Score,
    string SearchType,
    Guid? ParentChunkId = null,
    int ChunkLevel = 0
);

public record RankedResult(
    Guid DocumentId,
    string FileName,
    string Content,
    int Rank,
    double RawScore,
    string SearchType
);

public record TokenBudget(
    int TotalBudget,
    int SystemTokens,
    int RagTokens,
    int HistoryTokens,
    int QueryTokens,
    int ResponseReserve
);

public record ConfidenceResult(
    double Overall,
    double RetrievalQuality,
    double SourceCoverage,
    string Level  // "high" | "medium" | "low"
);

// ── Extended Ollama options with context window ─────────

public record OllamaOptionsExtended(
    [property: System.Text.Json.Serialization.JsonPropertyName("temperature")] double Temperature,
    [property: System.Text.Json.Serialization.JsonPropertyName("num_predict")] int NumPredict,
    [property: System.Text.Json.Serialization.JsonPropertyName("num_ctx")] int? NumCtx = null
);

public record OllamaChatRequestExtended(
    [property: System.Text.Json.Serialization.JsonPropertyName("model")] string Model,
    [property: System.Text.Json.Serialization.JsonPropertyName("messages")] List<OllamaMessage> Messages,
    [property: System.Text.Json.Serialization.JsonPropertyName("stream")] bool Stream,
    [property: System.Text.Json.Serialization.JsonPropertyName("options")] OllamaOptionsExtended Options
);
