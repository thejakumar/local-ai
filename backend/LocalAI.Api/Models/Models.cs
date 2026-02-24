using Pgvector;
using System.Text.Json.Serialization;

namespace LocalAI.Api.Models;

// ── Domain Models ────────────────────────────────────

public class Document
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FileName { get; set; } = "";
    public string FileType { get; set; } = "";   // code | pdf | text | markdown
    public int ChunkIndex { get; set; }
    public string Content { get; set; } = "";
    public Vector? Embedding { get; set; }

    [JsonIgnore]
    public string SearchVector { get; set; } = "";

    public Dictionary<string, string> Metadata { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Parent-child chunking support
    public Guid? ParentChunkId { get; set; }
    public int ChunkLevel { get; set; } = 0;  // 0 = parent/flat, 1 = child
}

public class Conversation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "New Chat";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<Message> Messages { get; set; } = [];
    public string? Summary { get; set; }
}

public class Message
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConversationId { get; set; }
    public Conversation Conversation { get; set; } = null!;
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
    public string? Model { get; set; }
    public int? TokensUsed { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class MessageFeedback
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MessageId { get; set; }
    public Message Message { get; set; } = null!;
    public bool Helpful { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// ── Request DTOs ─────────────────────────────────────

public record ChatRequest(
    Guid? ConversationId,
    string Message,
    bool UseRag = true,
    string? RagMode = "hybrid",
    string? Model = null,
    string? FileTypeFilter = null,
    string? FileNameFilter = null
);

public record IngestRequest(
    string FileName,
    string Content,
    string FileType = "text"
);

public record FeedbackRequest(
    Guid MessageId,
    bool Helpful
);

// ── Response DTOs ────────────────────────────────────

public record ChatResponse(
    Guid ConversationId,
    string Response,
    List<RagSource> Sources,
    string Model,
    int TokensUsed
);

public record RagSource(
    string FileName,
    string Snippet,
    double Similarity,
    string SearchType = "semantic"
);

public record ConversationSummary(
    Guid Id,
    string Title,
    DateTime UpdatedAt,
    int MessageCount
);

public record IngestResponse(
    string FileName,
    int ChunksCreated,
    string FileType
);

// ── Ollama API shapes ────────────────────────────────

public record OllamaMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content
);

public record OllamaChatRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] List<OllamaMessage> Messages,
    [property: JsonPropertyName("stream")] bool Stream,
    [property: JsonPropertyName("options")] OllamaOptions Options
);

public record OllamaOptions(
    [property: JsonPropertyName("temperature")] double Temperature,
    [property: JsonPropertyName("num_predict")] int NumPredict
);

public record OllamaEmbedRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("prompt")] string Prompt
);

public record OllamaEmbedResponse(
    [property: JsonPropertyName("embedding")] float[] Embedding
);

public record OllamaChatChunk(
    [property: JsonPropertyName("message")] OllamaMessage? MessageChunk,
    [property: JsonPropertyName("done")] bool Done
);
