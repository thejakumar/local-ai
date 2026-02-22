using LocalAI.Api.Data;
using LocalAI.Api.Models;
using LocalAI.Api.Services.Embedding;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using UglyToad.PdfPig;

namespace LocalAI.Api.Services.Rag;

public interface IRagService
{
    Task<IngestResponse> IngestFileAsync(string fileName, Stream content, CancellationToken ct = default);
    Task<IngestResponse> IngestTextAsync(string fileName, string content, string fileType = "text", CancellationToken ct = default);
    Task<List<RagSource>> SearchAsync(string query, int? topK = null, CancellationToken ct = default);
    Task<string> BuildContextAsync(string query, CancellationToken ct = default);
}

public class RagService(
    AppDbContext db,
    IEmbeddingService embeddingService,
    IConfiguration config,
    ILogger<RagService> logger) : IRagService
{
    private readonly int _chunkSize = int.Parse(config["Rag:ChunkSize"] ?? "512");
    private readonly int _chunkOverlap = int.Parse(config["Rag:ChunkOverlap"] ?? "64");
    private readonly int _topK = int.Parse(config["Rag:TopK"] ?? "5");
    private readonly double _minSimilarity = double.Parse(config["Rag:MinSimilarity"] ?? "0.65");

    // ── Ingest from file stream (PDF or text/code) ───
    public async Task<IngestResponse> IngestFileAsync(
        string fileName,
        Stream content,
        CancellationToken ct = default)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();

        string rawText;
        string fileType;

        if (ext == ".pdf")
        {
            rawText = ExtractPdfText(content);
            fileType = "pdf";
        }
        else
        {
            using var reader = new StreamReader(content);
            rawText = await reader.ReadToEndAsync(ct);
            fileType = IsCodeFile(ext) ? "code" : "text";
        }

        return await IngestTextAsync(fileName, rawText, fileType, ct);
    }

    // ── Ingest raw text ──────────────────────────────
    public async Task<IngestResponse> IngestTextAsync(
        string fileName,
        string content,
        string fileType = "text",
        CancellationToken ct = default)
    {
        // Remove existing chunks for this file
        var existing = await db.Documents
            .Where(d => d.FileName == fileName)
            .ToListAsync(ct);
        db.Documents.RemoveRange(existing);

        // Chunk the content
        var chunks = ChunkText(content);
        logger.LogInformation("Ingesting {File}: {Count} chunks", fileName, chunks.Count);

        var documents = new List<Document>();

        for (int i = 0; i < chunks.Count; i++)
        {
            var embedding = await embeddingService.GetEmbeddingAsync(chunks[i], ct);

            documents.Add(new Document
            {
                FileName = fileName,
                FileType = fileType,
                ChunkIndex = i,
                Content = chunks[i],
                Embedding = new Vector(embedding),
                Metadata = new Dictionary<string, string>
                {
                    ["total_chunks"] = chunks.Count.ToString(),
                    ["file_type"] = fileType
                }
            });
        }

        await db.Documents.AddRangeAsync(documents, ct);
        await db.SaveChangesAsync(ct);

        return new IngestResponse(fileName, chunks.Count, fileType);
    }

    // ── Semantic search ──────────────────────────────
    public async Task<List<RagSource>> SearchAsync(
        string query,
        int? topK = null,
        CancellationToken ct = default)
    {
        var k = topK ?? _topK;
        var queryEmbedding = await embeddingService.GetEmbeddingAsync(query, ct);
        var queryVector = new Vector(queryEmbedding);

        // Cosine similarity search via pgvector
        var results = await db.Documents
            .OrderBy(d => d.Embedding!.CosineDistance(queryVector))
            .Take(k * 2)  // over-fetch, then filter by similarity
            .Select(d => new
            {
                d.FileName,
                d.Content,
                Similarity = 1 - d.Embedding!.CosineDistance(queryVector)
            })
            .ToListAsync(ct);

        return results
            .Where(r => r.Similarity >= _minSimilarity)
            .Take(k)
            .Select(r => new RagSource(
                r.FileName,
                r.Content.Length > 300 ? r.Content[..300] + "..." : r.Content,
                Math.Round(r.Similarity, 3)
            ))
            .ToList();
    }

    // ── Build prompt context from search results ─────
    public async Task<string> BuildContextAsync(string query, CancellationToken ct = default)
    {
        var sources = await SearchAsync(query, ct: ct);
        if (sources.Count == 0) return "";

        var ctx = sources
            .Select((s, i) =>
                $"[Source {i + 1}: {s.FileName} (similarity: {s.Similarity})]\n{s.Snippet}")
            .Aggregate((a, b) => $"{a}\n\n{b}");

        return $"""
            RELEVANT CONTEXT FROM YOUR CODEBASE/DOCS:
            {ctx}

            Use the above context to answer the question. If the context doesn't contain 
            enough information, say so clearly and answer from general knowledge.

            """;
    }

    // ── Text chunking with overlap ───────────────────
    private List<string> ChunkText(string text)
    {
        var chunks = new List<string>();
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        int i = 0;
        while (i < words.Length)
        {
            var chunkWords = words.Skip(i).Take(_chunkSize).ToArray();
            chunks.Add(string.Join(" ", chunkWords));
            i += _chunkSize - _chunkOverlap;
        }

        return chunks;
    }

    // ── PDF text extraction ──────────────────────────
    private static string ExtractPdfText(Stream stream)
    {
        using var pdf = PdfDocument.Open(stream);
        return string.Join("\n", pdf.GetPages().Select(p => p.Text));
    }

    // ── Detect code files ────────────────────────────
    private static bool IsCodeFile(string ext) =>
        ext is ".cs" or ".ts" or ".js" or ".py" or ".java" or ".go"
            or ".rs" or ".cpp" or ".h" or ".jsx" or ".tsx" or ".vue"
            or ".html" or ".css" or ".scss" or ".json" or ".yaml" or ".yml";
}