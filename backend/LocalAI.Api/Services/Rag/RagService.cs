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
    Task<List<RagSource>> SearchAsync(string query, string? mode = null, int? topK = null, CancellationToken ct = default);
    Task<string> BuildContextAsync(string query, string? mode = null, CancellationToken ct = default);
}

public class RagService(
    AppDbContext db,
    IEmbeddingService embeddingService,
    IChunkingService chunkingService,
    IConfiguration config,
    ILogger<RagService> logger) : IRagService
{
    private readonly int _topK = int.Parse(config["Rag:TopK"] ?? "5");
    private readonly double _minSimilarity = double.Parse(config["Rag:MinSimilarity"] ?? "0.65");
    private readonly string _defaultSearchMode = config["Rag:DefaultSearchMode"] ?? "hybrid";
    private readonly double _semanticWeight = double.Parse(config["Rag:SemanticWeight"] ?? "0.6");
    private readonly double _keywordWeight = double.Parse(config["Rag:KeywordWeight"] ?? "0.4");

    // ── Ingest from file stream (PDF or text/code) ───
    public async Task<IngestResponse> IngestFileAsync(
        string fileName,
        Stream content,
        CancellationToken ct = default)
    {
        // Reset stream position to beginning
        if (content.CanSeek)
            content.Seek(0, SeekOrigin.Begin);

        var ext = Path.GetExtension(fileName).ToLowerInvariant();

        string rawText;
        string fileType;

        if (ext == ".pdf")
        {
            rawText = ExtractPdfText(content);
            fileType = "pdf";
        }
        else if (ext is ".md" or ".markdown")
        {
            using var reader = new StreamReader(content);
            rawText = await reader.ReadToEndAsync(ct);
            fileType = "markdown";
        }
        else
        {
            using var reader = new StreamReader(content);
            rawText = await reader.ReadToEndAsync(ct);
            fileType = IsCodeFile(ext) ? "code" : "text";
        }

        return await IngestTextAsync(fileName, rawText, fileType, ct);
    }

    // ── Ingest raw text with batch embeddings ────────
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

        // Use smart chunking
        var chunks = chunkingService.ChunkText(content, fileType);
        logger.LogInformation("Ingesting {File} ({Type}): {Count} chunks", fileName, fileType, chunks.Count);

        // Get embeddings in batch (much faster!)
        var embeddings = await embeddingService.GetBatchEmbeddingsAsync(chunks, ct);

        var documents = new List<Document>();

        for (int i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            var embedding = embeddings[i];

            // Generate full-text search vector using PostgreSQL
            var searchVector = GenerateSearchVector(chunk);

            documents.Add(new Document
            {
                FileName = fileName,
                FileType = fileType,
                ChunkIndex = i,
                Content = chunk,
                Embedding = new Vector(embedding),
                SearchVector = searchVector,
                Metadata = new Dictionary<string, string>
                {
                    ["total_chunks"] = chunks.Count.ToString(),
                    ["file_type"] = fileType,
                    ["chunk_size"] = chunk.Length.ToString()
                }
            });
        }

        await db.Documents.AddRangeAsync(documents, ct);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Ingestion complete: {File} - {Count} chunks with embeddings", fileName, chunks.Count);
        return new IngestResponse(fileName, chunks.Count, fileType);
    }

    // ── Hybrid semantic + keyword search ─────────────
    public async Task<List<RagSource>> SearchAsync(
        string query,
        string? mode = null,
        int? topK = null,
        CancellationToken ct = default)
    {
        var k = topK ?? _topK;
        var searchMode = mode ?? _defaultSearchMode;

        logger.LogInformation("Search query='{Query}' mode={Mode} topK={K}", query, searchMode, k);

        var results = searchMode switch
        {
            "semantic" => await SemanticSearchAsync(query, k, ct),
            "keyword" => await KeywordSearchAsync(query, k, ct),
            "hybrid" or _ => await HybridSearchAsync(query, k, ct)
        };

        // Filter by similarity threshold
        return results
            .Where(r => r.Similarity >= _minSimilarity)
            .Take(k)
            .ToList();
    }

    // ── Semantic search via cosine distance ──────────
    private async Task<List<RagSource>> SemanticSearchAsync(
        string query,
        int k,
        CancellationToken ct)
    {
        var queryEmbedding = await embeddingService.GetEmbeddingAsync(query, ct);
        var queryVector = new Vector(queryEmbedding);

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
            .Select(r => new RagSource(
                r.FileName,
                r.Content.Length > 300 ? r.Content[..300] + "..." : r.Content,
                Math.Round(r.Similarity, 3),
                "semantic"
            ))
            .ToList();
    }

    // ── Keyword search via PostgreSQL full-text search ────
    private async Task<List<RagSource>> KeywordSearchAsync(
        string query,
        int k,
        CancellationToken ct)
    {
        // Use PostgreSQL full-text search (lexeme-based, case-insensitive)
        // Formulate as: query @@ plainto_tsquery('english', 'search terms')
        
        var results = await db.Documents
            .FromSqlInterpolated($@"
                SELECT d.""Id"", d.""FileName"", d.""FileType"", d.""ChunkIndex"", 
                       d.""Content"", d.""Embedding"", d.""SearchVector"", d.""Metadata"", d.""CreatedAt""
                FROM ""Documents"" d
                WHERE to_tsvector('english', d.""Content"") @@ plainto_tsquery('english', {query})
                ORDER BY ts_rank(to_tsvector('english', d.""Content""), plainto_tsquery('english', {query})) DESC
                LIMIT {k * 2}
            ")
            .Select(d => new
            {
                d.FileName,
                d.Content
            })
            .ToListAsync(ct);

        // Assign equal scores for keyword results
        return results
            .Take(k)
            .Select((r, i) => new RagSource(
                r.FileName,
                r.Content.Length > 300 ? r.Content[..300] + "..." : r.Content,
                1.0 - (i * 0.1),  // Decreasing relevance by position
                "keyword"
            ))
            .ToList();
    }

    // ── Hybrid search: combine semantic + keyword ────
    private async Task<List<RagSource>> HybridSearchAsync(
        string query,
        int k,
        CancellationToken ct)
    {
        // Run searches sequentially instead of in parallel
        var semanticResults = await SemanticSearchAsync(query, k * 2, ct);
        var keywordResults = await KeywordSearchAsync(query, k * 2, ct);

        // Combine and deduplicate by content, give semantic weight priority
        var combined = new Dictionary<string, RagSource>();

        // Add semantic results with full weight
        foreach (var result in semanticResults)
        {
            var key = result.Snippet[..Math.Min(50, result.Snippet.Length)];  // Use first 50 chars as dedup key
            combined[key] = new RagSource(
                result.FileName,
                result.Snippet,
                result.Similarity * _semanticWeight + 0.2,  // Semantic weight + base boost
                "hybrid"
            );
        }

        // Add keyword results, boost if not already in semantic results
        foreach (var result in keywordResults)
        {
            var key = result.Snippet[..Math.Min(50, result.Snippet.Length)];
            if (!combined.ContainsKey(key))
            {
                combined[key] = new RagSource(
                    result.FileName,
                    result.Snippet,
                    result.Similarity * _keywordWeight,  // Keyword weight (lower priority)
                    "hybrid"
                );
            }
        }

        return combined.Values
            .OrderByDescending(r => r.Similarity)
            .Take(k)
            .ToList();
    }

    // ── Build prompt context from search results ─────
    public async Task<string> BuildContextAsync(
        string query,
        string? mode = null,
        CancellationToken ct = default)
    {
        var sources = await SearchAsync(query, mode, ct: ct);
        if (sources.Count == 0) return "";

        var ctx = sources
            .Select((s, i) =>
                $"[Source {i + 1}: {s.FileName} ({s.SearchType}, similarity: {s.Similarity})]\n{s.Snippet}")
            .Aggregate((a, b) => $"{a}\n\n{b}");

        return $"""
            RELEVANT CONTEXT FROM YOUR CODEBASE/DOCS:
            {ctx}

            Use the above context to answer the question. If the context doesn't contain 
            enough information, say so clearly and answer from general knowledge.
            """;
    }

    // ── PostgreSQL full-text search vector generation ──
    private static string GenerateSearchVector(string text)
    {
        // In production, this would use PostgreSQL's to_tsvector directly
        // For now, create a simple searchable representation
        return string.Join(" ", text.Split(' ').Where(w => w.Length > 2).Take(50));
    }

    // ── PDF text extraction ──────────────────────────
    private static string ExtractPdfText(Stream stream)
    {
        try
        {
            // Reset stream position to beginning
            if (stream.CanSeek)
                stream.Seek(0, SeekOrigin.Begin);

            using var pdf = PdfDocument.Open(stream);
            var text = string.Join("\n", pdf.GetPages().Select(p => p.Text));

            return string.IsNullOrWhiteSpace(text) ? "" : text;
        }
        catch (Exception ex)
        {
            // Log extraction failure - PDF may be corrupted or unsupported
            return "";
        }
    }

    // ── Detect code files ────────────────────────────
    private static bool IsCodeFile(string ext) =>
        ext is ".cs" or ".ts" or ".js" or ".py" or ".java" or ".go"
            or ".rs" or ".cpp" or ".h" or ".jsx" or ".tsx" or ".vue"
            or ".html" or ".css" or ".scss" or ".json" or ".yaml" or ".yml";
}
