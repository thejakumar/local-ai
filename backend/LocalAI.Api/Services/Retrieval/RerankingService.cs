using LocalAI.Api.Models;
using LocalAI.Api.Services.Ollama;

namespace LocalAI.Api.Services.Retrieval;

public interface IRerankingService
{
    Task<List<RankedResult>> RerankAsync(string query, List<RankedResult> candidates, int finalK, CancellationToken ct = default);
}

public class RerankingService(
    IOllamaService ollama,
    IConfiguration config,
    ILogger<RerankingService> logger) : IRerankingService
{
    private readonly int _concurrency = int.Parse(config["Retrieval:RerankConcurrency"] ?? "3");

    private const string RerankPrompt = """
        Rate the relevance of the following passage to the question on a scale of 0 to 10, where 0 means completely irrelevant and 10 means perfectly answers the question.
        Return ONLY a single number, nothing else.

        Question: {QUERY}

        Passage: {PASSAGE}

        Relevance score (0-10):
        """;

    public async Task<List<RankedResult>> RerankAsync(
        string query,
        List<RankedResult> candidates,
        int finalK,
        CancellationToken ct = default)
    {
        if (candidates.Count == 0) return [];
        if (candidates.Count <= finalK) return candidates;

        logger.LogInformation("Reranking {Count} candidates for query: '{Query}'",
            candidates.Count, query[..Math.Min(50, query.Length)]);

        var semaphore = new SemaphoreSlim(_concurrency);
        var scores = new (RankedResult Result, double Score)[candidates.Count];

        var tasks = candidates.Select(async (candidate, index) =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var score = await ScoreCandidateAsync(query, candidate.Content, ct);
                scores[index] = (candidate, score);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Reranking failed for candidate {Index}, assigning score 0", index);
                scores[index] = (candidate, 0);
            }
            finally
            {
                semaphore.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks);

        var reranked = scores
            .OrderByDescending(s => s.Score)
            .Take(finalK)
            .Select((s, i) => new RankedResult(
                s.Result.DocumentId,
                s.Result.FileName,
                s.Result.Content,
                i + 1,
                s.Score / 10.0,  // Normalize to [0,1]
                s.Result.SearchType
            ))
            .ToList();

        logger.LogInformation("Reranking complete: top scores = [{Scores}]",
            string.Join(", ", reranked.Take(3).Select(r => $"{r.RawScore:F2}")));

        return reranked;
    }

    private async Task<double> ScoreCandidateAsync(string query, string passage, CancellationToken ct)
    {
        // Truncate passage to avoid wasting tokens
        var truncatedPassage = passage.Length > 1000 ? passage[..1000] + "..." : passage;

        var prompt = RerankPrompt
            .Replace("{QUERY}", query)
            .Replace("{PASSAGE}", truncatedPassage);

        var messages = new List<OllamaMessage>
        {
            new("user", prompt)
        };

        var result = await ollama.ChatAsync(messages, temperature: 0.0, maxTokens: 5, ct: ct);

        // Parse numeric score from response
        var cleaned = new string(result.Where(c => char.IsDigit(c) || c == '.').ToArray());
        if (double.TryParse(cleaned, out var score))
            return Math.Clamp(score, 0, 10);

        logger.LogWarning("Could not parse reranking score from: '{Result}', defaulting to 5", result);
        return 5.0;
    }
}
