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
    Task<List<RagSource>> SearchWithFiltersAsync(string query, RetrievalOptions options, CancellationToken ct = default);
    Task<List<RankedResult>> SemanticSearchRankedAsync(string query, int limit, RetrievalOptions? options = null, CancellationToken ct = default);
    Task<List<RankedResult>> SemanticSearchByEmbeddingAsync(float[] queryEmbedding, int limit, RetrievalOptions? options = null, CancellationToken ct = default);
    Task<List<RankedResult>> KeywordSearchRankedAsync(string query, int limit, RetrievalOptions? options = null, CancellationToken ct = default);
}

public class RagService(
    AppDbContext db,
    IEmbeddingService embeddingService,
    IChunkingService chunkingService,
    IConfiguration config,
    ILogger<RagService> logger) : IRagService
{
    private readonly int _topK = int.Parse(config["Rag:TopK"] ?? "5");
    private readonly double _minSimilarity = double.Parse(config["Rag:MinSimilarity"] ?? "0.3");
    private readonly string _defaultSearchMode = config["Rag:DefaultSearchMode"] ?? "hybrid";
    private readonly int _rrfK = int.Parse(config["Rag:RrfK"] ?? "60");
    private readonly bool _enableParentChild = bool.Parse(config["Retrieval:EnableParentChildChunking"] ?? "true");
    private readonly int _parentChunkSize = int.Parse(config["Retrieval:ParentChunkSize"] ?? "1500");
    private readonly int _childChunkSize = int.Parse(config["Retrieval:ChildChunkSize"] ?? "400");
    private readonly int _childChunkOverlap = int.Parse(config["Retrieval:ChildChunkOverlap"] ?? "100");

    // ── Ingest from file stream (PDF or text/code) ───
    public async Task<IngestResponse> IngestFileAsync(
        string fileName,
        Stream content,
        CancellationToken ct = default)
    {
        if (content.CanSeek)
            content.Seek(0, SeekOrigin.Begin);

        var ext = Path.GetExtension(fileName).ToLowerInvariant();

        string rawText;
        string fileType;

        if (ext == ".pdf")
        {
            rawText = ExtractPdfText(content);
            fileType = "pdf";
            if (string.IsNullOrWhiteSpace(rawText))
            {
                logger.LogWarning("PDF extraction returned empty text for {File} — file may be scanned/image-based", fileName);
                return new IngestResponse(fileName, 0, fileType);
            }
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

    // ── Ingest raw text with parent-child chunking + batch embeddings ────
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

        if (_enableParentChild)
            return await IngestWithParentChildAsync(fileName, content, fileType, ct);

        return await IngestFlatAsync(fileName, content, fileType, ct);
    }

    /// <summary>
    /// Parent-child chunking: large parent chunks for context, small child chunks for precise search.
    /// </summary>
    private async Task<IngestResponse> IngestWithParentChildAsync(
        string fileName, string content, string fileType, CancellationToken ct)
    {
        // Create parent chunks (large, for context delivery)
        var parentChunks = chunkingService.ChunkText(content, fileType, _parentChunkSize, 200);
        logger.LogInformation("Ingesting {File} ({Type}): {Count} parent chunks", fileName, fileType, parentChunks.Count);

        var allChildTexts = new List<string>();
        var childToParentMap = new List<(int childIndex, int parentIndex)>();

        // Create parent documents (no embedding)
        var parentDocs = new List<Document>();
        for (int p = 0; p < parentChunks.Count; p++)
        {
            var parentDoc = new Document
            {
                FileName = fileName,
                FileType = fileType,
                ChunkIndex = p,
                Content = parentChunks[p],
                Embedding = null,
                ChunkLevel = 0,
                ParentChunkId = null,
                SearchVector = "",
                Metadata = new Dictionary<string, string>
                {
                    ["total_parent_chunks"] = parentChunks.Count.ToString(),
                    ["file_type"] = fileType,
                    ["chunk_level"] = "parent"
                }
            };
            parentDocs.Add(parentDoc);
        }

        // Create child chunks from each parent
        for (int p = 0; p < parentChunks.Count; p++)
        {
            var children = chunkingService.ChunkByCharacters(parentChunks[p], _childChunkSize, _childChunkOverlap);
            foreach (var child in children)
            {
                var childIndex = allChildTexts.Count;
                allChildTexts.Add(child);
                childToParentMap.Add((childIndex, p));
            }
        }

        logger.LogInformation("Created {ChildCount} child chunks from {ParentCount} parents for {File}",
            allChildTexts.Count, parentChunks.Count, fileName);

        // Batch embed all child chunks
        var embeddings = await embeddingService.GetBatchEmbeddingsAsync(allChildTexts, ct);

        // Build child documents
        var childDocs = new List<Document>();
        for (int c = 0; c < allChildTexts.Count; c++)
        {
            var (_, parentIdx) = childToParentMap[c];
            var embedding = embeddings[c];

            if (embedding == null || embedding.Length == 0)
            {
                logger.LogWarning("Skipping child chunk {Index} due to empty embedding", c);
                continue;
            }

            childDocs.Add(new Document
            {
                FileName = fileName,
                FileType = fileType,
                ChunkIndex = c,
                Content = allChildTexts[c],
                Embedding = new Vector(embedding),
                ChunkLevel = 1,
                ParentChunkId = parentDocs[parentIdx].Id,
                SearchVector = "",
                Metadata = new Dictionary<string, string>
                {
                    ["file_type"] = fileType,
                    ["chunk_level"] = "child",
                    ["parent_index"] = parentIdx.ToString()
                }
            });
        }

        await db.Documents.AddRangeAsync(parentDocs, ct);
        await db.Documents.AddRangeAsync(childDocs, ct);
        await db.SaveChangesAsync(ct);

        await UpdateSearchVectorsAsync(ct);

        logger.LogInformation("Ingestion complete: {File} - {Parents} parents + {Children} children",
            fileName, parentDocs.Count, childDocs.Count);
        return new IngestResponse(fileName, parentDocs.Count + childDocs.Count, fileType);
    }

    /// <summary>
    /// Flat chunking (used when parent-child is disabled).
    /// </summary>
    private async Task<IngestResponse> IngestFlatAsync(
        string fileName, string content, string fileType, CancellationToken ct)
    {
        var chunks = chunkingService.ChunkText(content, fileType);
        logger.LogInformation("Ingesting {File} ({Type}): {Count} chunks", fileName, fileType, chunks.Count);

        var embeddings = await embeddingService.GetBatchEmbeddingsAsync(chunks, ct);

        var documents = new List<Document>();
        for (int i = 0; i < chunks.Count; i++)
        {
            var embedding = embeddings[i];
            if (embedding == null || embedding.Length == 0)
            {
                logger.LogWarning("Skipping chunk {Index} due to empty embedding", i);
                continue;
            }

            documents.Add(new Document
            {
                FileName = fileName,
                FileType = fileType,
                ChunkIndex = i,
                Content = chunks[i],
                Embedding = new Vector(embedding),
                ChunkLevel = 0,
                SearchVector = "",
                Metadata = new Dictionary<string, string>
                {
                    ["total_chunks"] = chunks.Count.ToString(),
                    ["file_type"] = fileType,
                    ["chunk_size"] = chunks[i].Length.ToString()
                }
            });
        }

        await db.Documents.AddRangeAsync(documents, ct);
        await db.SaveChangesAsync(ct);
        await UpdateSearchVectorsAsync(ct);

        logger.LogInformation("Ingestion complete: {File} - {Count} chunks with embeddings", fileName, documents.Count);
        return new IngestResponse(fileName, documents.Count, fileType);
    }

    private async Task UpdateSearchVectorsAsync(CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync(
            @"UPDATE ""Documents"" SET ""SearchVector"" = to_tsvector('english', ""Content"") WHERE ""SearchVector"" = '' OR ""SearchVector"" IS NULL",
            ct);
    }

    // ── Search (backward compatible) ─────────────────
    public async Task<List<RagSource>> SearchAsync(
        string query, string? mode = null, int? topK = null, CancellationToken ct = default)
    {
        return await SearchWithFiltersAsync(query, new RetrievalOptions(
            SearchMode: mode ?? _defaultSearchMode,
            TopK: topK ?? _topK,
            EnableQueryExpansion: false,
            EnableHyde: false,
            EnableReranking: false
        ), ct);
    }

    // ── Search with filters ──────────────────────────
    public async Task<List<RagSource>> SearchWithFiltersAsync(
        string query, RetrievalOptions options, CancellationToken ct = default)
    {
        var k = options.TopK;
        var searchMode = options.SearchMode;

        logger.LogInformation("Search query='{Query}' mode={Mode} topK={K}", query, searchMode, k);

        var results = searchMode switch
        {
            "semantic" => await SemanticSearchAsync(query, k * 3, options, ct),
            "keyword" => await KeywordSearchAsync(query, k * 3, options, ct),
            "hybrid" or _ => await HybridSearchAsync(query, k, options, ct)
        };

        return results
            .Where(r => r.Similarity >= _minSimilarity)
            .Take(k)
            .ToList();
    }

    // ── Semantic search (ranked) ─────────────────────
    public async Task<List<RankedResult>> SemanticSearchRankedAsync(
        string query, int limit, RetrievalOptions? options = null, CancellationToken ct = default)
    {
        var queryEmbedding = await embeddingService.GetEmbeddingAsync(query, ct);
        return await SemanticSearchByEmbeddingAsync(queryEmbedding, limit, options, ct);
    }

    public async Task<List<RankedResult>> SemanticSearchByEmbeddingAsync(
        float[] queryEmbedding, int limit, RetrievalOptions? options = null, CancellationToken ct = default)
    {
        var queryVector = new Vector(queryEmbedding);

        IQueryable<Document> baseQuery = db.Documents.Where(d => d.Embedding != null);

        // Only filter for child chunks if child chunks actually exist
        if (_enableParentChild)
        {
            var hasChildChunks = await db.Documents.AnyAsync(d => d.ChunkLevel == 1, ct);
            if (hasChildChunks)
                baseQuery = baseQuery.Where(d => d.ChunkLevel == 1);
        }

        baseQuery = ApplyFilters(baseQuery, options);

        var results = await baseQuery
            .OrderBy(d => d.Embedding!.CosineDistance(queryVector))
            .Take(limit)
            .Select(d => new
            {
                d.Id,
                d.FileName,
                d.Content,
                d.ChunkIndex,
                d.ParentChunkId,
                d.ChunkLevel,
                Similarity = 1 - d.Embedding!.CosineDistance(queryVector)
            })
            .ToListAsync(ct);

        return results.Select((r, i) => new RankedResult(
            r.Id, r.FileName, r.Content, i + 1, r.Similarity, "semantic"
        )).ToList();
    }

    private async Task<List<RagSource>> SemanticSearchAsync(
        string query, int limit, RetrievalOptions? options, CancellationToken ct)
    {
        var ranked = await SemanticSearchRankedAsync(query, limit, options, ct);
        return await ResolveToRagSourcesAsync(ranked, ct);
    }

    // ── Keyword search (ranked) ──────────────────────
    public async Task<List<RankedResult>> KeywordSearchRankedAsync(
        string query, int limit, RetrievalOptions? options = null, CancellationToken ct = default)
    {
        // Only filter for child chunks if they exist
        var chunkLevelFilter = -1; // -1 means no filter
        if (_enableParentChild)
        {
            var hasChildChunks = await db.Documents.AnyAsync(d => d.ChunkLevel == 1, ct);
            if (hasChildChunks)
                chunkLevelFilter = 1;
        }

        var results = await db.Documents
            .FromSqlInterpolated($@"
                SELECT d.""Id"", d.""FileName"", d.""FileType"", d.""ChunkIndex"",
                       d.""Content"", d.""Embedding"", d.""SearchVector"", d.""Metadata"",
                       d.""CreatedAt"", d.""ParentChunkId"", d.""ChunkLevel""
                FROM ""Documents"" d
                WHERE to_tsvector('english', d.""Content"") @@ plainto_tsquery('english', {query})
                  AND ({chunkLevelFilter} = -1 OR d.""ChunkLevel"" = {chunkLevelFilter})
                ORDER BY ts_rank(to_tsvector('english', d.""Content""), plainto_tsquery('english', {query})) DESC
                LIMIT {limit}
            ")
            .Select(d => new { d.Id, d.FileName, d.Content, d.ChunkIndex, d.ParentChunkId, d.ChunkLevel })
            .ToListAsync(ct);

        return results.Select((r, i) => new RankedResult(
            r.Id, r.FileName, r.Content, i + 1, 0, "keyword"
        )).ToList();
    }

    private async Task<List<RagSource>> KeywordSearchAsync(
        string query, int limit, RetrievalOptions? options, CancellationToken ct)
    {
        var ranked = await KeywordSearchRankedAsync(query, limit, options, ct);
        return await ResolveToRagSourcesAsync(ranked, ct);
    }

    // ── Hybrid search using Reciprocal Rank Fusion ───
    private async Task<List<RagSource>> HybridSearchAsync(
        string query, int k, RetrievalOptions? options, CancellationToken ct)
    {
        var fetchLimit = k * 4;

        // Run both searches in parallel
        var semanticTask = SemanticSearchRankedAsync(query, fetchLimit, options, ct);
        var keywordTask = KeywordSearchRankedAsync(query, fetchLimit, options, ct);
        await Task.WhenAll(semanticTask, keywordTask);

        var semanticResults = semanticTask.Result;
        var keywordResults = keywordTask.Result;

        // Reciprocal Rank Fusion
        var rrfScores = new Dictionary<Guid, (double Score, RankedResult Result)>();

        foreach (var result in semanticResults)
        {
            var rrfScore = 1.0 / (_rrfK + result.Rank);
            rrfScores[result.DocumentId] = (rrfScore, result);
        }

        foreach (var result in keywordResults)
        {
            var rrfScore = 1.0 / (_rrfK + result.Rank);
            if (rrfScores.TryGetValue(result.DocumentId, out var existing))
                rrfScores[result.DocumentId] = (existing.Score + rrfScore, existing.Result);
            else
                rrfScores[result.DocumentId] = (rrfScore, result);
        }

        var sorted = rrfScores.Values.OrderByDescending(v => v.Score).ToList();
        var maxScore = sorted.Count > 0 ? sorted[0].Score : 1.0;

        var fusedResults = sorted
            .Take(k * 2)
            .Select((v, i) => new RankedResult(
                v.Result.DocumentId, v.Result.FileName, v.Result.Content,
                i + 1, v.Score / maxScore, "hybrid"
            ))
            .ToList();

        return await ResolveToRagSourcesAsync(fusedResults, ct);
    }

    // ── Resolve child chunks to parent content ───────
    private async Task<List<RagSource>> ResolveToRagSourcesAsync(List<RankedResult> results, CancellationToken ct)
    {
        if (!_enableParentChild)
        {
            return results.Select(r => new RagSource(
                r.FileName, r.Content, Math.Round(r.RawScore, 3), r.SearchType
            )).ToList();
        }

        var docIds = results.Select(r => r.DocumentId).ToList();
        var childDocs = await db.Documents
            .Where(d => docIds.Contains(d.Id))
            .Select(d => new { d.Id, d.ParentChunkId, d.FileName })
            .ToListAsync(ct);

        var parentIds = childDocs
            .Where(c => c.ParentChunkId.HasValue)
            .Select(c => c.ParentChunkId!.Value)
            .Distinct()
            .ToList();

        var parents = parentIds.Count > 0
            ? await db.Documents
                .Where(d => parentIds.Contains(d.Id))
                .ToDictionaryAsync(d => d.Id, d => d.Content, ct)
            : new Dictionary<Guid, string>();

        var seen = new HashSet<Guid>();
        var ragSources = new List<RagSource>();

        foreach (var result in results)
        {
            var child = childDocs.FirstOrDefault(c => c.Id == result.DocumentId);
            if (child?.ParentChunkId != null && parents.TryGetValue(child.ParentChunkId.Value, out var parentContent))
            {
                if (!seen.Add(child.ParentChunkId.Value)) continue;
                ragSources.Add(new RagSource(result.FileName, parentContent, Math.Round(result.RawScore, 3), result.SearchType));
            }
            else
            {
                if (!seen.Add(result.DocumentId)) continue;
                ragSources.Add(new RagSource(result.FileName, result.Content, Math.Round(result.RawScore, 3), result.SearchType));
            }
        }

        return ragSources;
    }

    // ── Apply metadata filters ───────────────────────
    private static IQueryable<Document> ApplyFilters(IQueryable<Document> query, RetrievalOptions? options)
    {
        if (options == null) return query;
        if (!string.IsNullOrEmpty(options.FileTypeFilter))
            query = query.Where(d => d.FileType == options.FileTypeFilter);
        if (!string.IsNullOrEmpty(options.FileNameFilter))
            query = query.Where(d => d.FileName.Contains(options.FileNameFilter));
        if (options.DateAfter.HasValue)
            query = query.Where(d => d.CreatedAt >= options.DateAfter.Value);
        if (options.DateBefore.HasValue)
            query = query.Where(d => d.CreatedAt <= options.DateBefore.Value);
        return query;
    }

    // ── PDF text extraction ──────────────────────────
    private string ExtractPdfText(Stream stream)
    {
        try
        {
            if (stream.CanSeek) stream.Seek(0, SeekOrigin.Begin);
            using var pdf = PdfDocument.Open(stream);
            var text = string.Join("\n", pdf.GetPages().Select(p => p.Text));
            return string.IsNullOrWhiteSpace(text) ? "" : text;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to extract PDF text");
            return "";
        }
    }

    private static bool IsCodeFile(string ext) =>
        ext is ".cs" or ".ts" or ".js" or ".py" or ".java" or ".go"
            or ".rs" or ".cpp" or ".h" or ".jsx" or ".tsx" or ".vue"
            or ".html" or ".css" or ".scss" or ".json" or ".yaml" or ".yml";
}
