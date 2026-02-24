using LocalAI.Api.Models;
using LocalAI.Api.Services.Embedding;
using LocalAI.Api.Services.Rag;

namespace LocalAI.Api.Services.Retrieval;

public interface IRetrievalPipeline
{
    Task<List<RagSource>> RetrieveAsync(string query, RetrievalOptions options, CancellationToken ct = default);
}

public class RetrievalPipeline(
    IRagService ragService,
    IEmbeddingService embeddingService,
    IHydeService hydeService,
    IQueryExpansionService queryExpansionService,
    IRerankingService rerankingService,
    IConfiguration config,
    ILogger<RetrievalPipeline> logger) : IRetrievalPipeline
{
    private readonly bool _enableHyde = bool.Parse(config["Retrieval:EnableHyde"] ?? "true");
    private readonly bool _enableQueryExpansion = bool.Parse(config["Retrieval:EnableQueryExpansion"] ?? "true");
    private readonly bool _enableReranking = bool.Parse(config["Retrieval:EnableReranking"] ?? "true");
    private readonly int _rerankTopK = int.Parse(config["Retrieval:RerankTopK"] ?? "20");
    private readonly int _rerankFinalK = int.Parse(config["Retrieval:RerankFinalK"] ?? "5");
    private readonly int _rrfK = int.Parse(config["Rag:RrfK"] ?? "60");
    private readonly double _minSimilarity = double.Parse(config["Rag:MinSimilarity"] ?? "0.3");

    public async Task<List<RagSource>> RetrieveAsync(
        string query,
        RetrievalOptions options,
        CancellationToken ct = default)
    {
        logger.LogInformation("Retrieval pipeline starting for: '{Query}' [HyDE={Hyde}, Expansion={Expand}, Rerank={Rerank}]",
            query[..Math.Min(50, query.Length)],
            options.EnableHyde && _enableHyde,
            options.EnableQueryExpansion && _enableQueryExpansion,
            options.EnableReranking && _enableReranking);

        // Stage 1: Generate query variants
        var queryVariants = new List<string> { query };

        // Query expansion — generate alternative phrasings
        if (options.EnableQueryExpansion && _enableQueryExpansion)
        {
            var expanded = await queryExpansionService.ExpandQueryAsync(query, ct);
            queryVariants.AddRange(expanded);
            logger.LogInformation("Query expansion: {Original} + {Expanded} variants", 1, expanded.Count);
        }

        // Stage 2: Generate embeddings for all query variants + HyDE
        var allRankedResults = new List<List<RankedResult>>();
        var fetchLimit = _enableReranking ? _rerankTopK : options.TopK * 3;

        // Semantic search for each query variant
        foreach (var variant in queryVariants)
        {
            var results = await ragService.SemanticSearchRankedAsync(variant, fetchLimit, options, ct);
            if (results.Count > 0) allRankedResults.Add(results);
        }

        // HyDE — search using hypothetical document embedding
        if (options.EnableHyde && _enableHyde)
        {
            var hypotheticalDoc = await hydeService.GenerateHypotheticalDocumentAsync(query, ct);
            var hydeEmbedding = await embeddingService.GetEmbeddingAsync(hypotheticalDoc, ct);
            var hydeResults = await ragService.SemanticSearchByEmbeddingAsync(hydeEmbedding, fetchLimit, options, ct);
            if (hydeResults.Count > 0) allRankedResults.Add(hydeResults);
            logger.LogInformation("HyDE search returned {Count} results", hydeResults.Count);
        }

        // Keyword search
        var keywordResults = await ragService.KeywordSearchRankedAsync(query, fetchLimit, options, ct);
        if (keywordResults.Count > 0) allRankedResults.Add(keywordResults);

        // Stage 3: Reciprocal Rank Fusion across ALL search result lists
        var fused = FuseWithRRF(allRankedResults);

        if (fused.Count == 0)
        {
            logger.LogInformation("No results found across all search strategies");
            return [];
        }

        logger.LogInformation("RRF fusion produced {Count} combined results from {Lists} search lists",
            fused.Count, allRankedResults.Count);

        // Stage 4: Reranking
        List<RankedResult> finalResults;
        if (options.EnableReranking && _enableReranking && fused.Count > _rerankFinalK)
        {
            var candidatesForRerank = fused.Take(_rerankTopK).ToList();
            finalResults = await rerankingService.RerankAsync(query, candidatesForRerank, _rerankFinalK, ct);
        }
        else
        {
            finalResults = fused.Take(options.TopK).ToList();
        }

        // Stage 5: Filter by minimum similarity and convert to RagSource
        var sources = finalResults
            .Where(r => r.RawScore >= _minSimilarity)
            .Take(options.TopK)
            .Select(r => new RagSource(
                r.FileName,
                r.Content,
                Math.Round(r.RawScore, 3),
                r.SearchType
            ))
            .ToList();

        logger.LogInformation("Retrieval pipeline complete: {Count} sources returned", sources.Count);
        return sources;
    }

    /// <summary>
    /// Reciprocal Rank Fusion across multiple ranked result lists.
    /// </summary>
    private List<RankedResult> FuseWithRRF(List<List<RankedResult>> rankedLists)
    {
        var rrfScores = new Dictionary<Guid, (double Score, RankedResult Result)>();

        foreach (var list in rankedLists)
        {
            foreach (var result in list)
            {
                var rrfScore = 1.0 / (_rrfK + result.Rank);
                if (rrfScores.TryGetValue(result.DocumentId, out var existing))
                    rrfScores[result.DocumentId] = (existing.Score + rrfScore, existing.Result);
                else
                    rrfScores[result.DocumentId] = (rrfScore, result);
            }
        }

        var sorted = rrfScores.Values.OrderByDescending(v => v.Score).ToList();
        var maxScore = sorted.Count > 0 ? sorted[0].Score : 1.0;

        return sorted.Select((v, i) => new RankedResult(
            v.Result.DocumentId,
            v.Result.FileName,
            v.Result.Content,
            i + 1,
            v.Score / maxScore,  // Normalize to [0,1]
            "hybrid"
        )).ToList();
    }
}
