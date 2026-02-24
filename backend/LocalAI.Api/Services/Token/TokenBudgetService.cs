using LocalAI.Api.Models;

namespace LocalAI.Api.Services.Token;

public interface ITokenBudgetService
{
    int EstimateTokens(string text);
    TokenBudget AllocateBudget(string systemPrompt, string userQuery, List<OllamaMessage> history, string ragContext);
    List<OllamaMessage> TrimHistory(List<OllamaMessage> messages, int maxTokens);
    string TrimRagContext(List<RagSource> sources, int maxTokens);
}

public class TokenBudgetService(IConfiguration config, ILogger<TokenBudgetService> logger) : ITokenBudgetService
{
    private readonly int _contextWindow = int.Parse(config["Ollama:ContextWindow"] ?? "8192");
    private readonly int _responseReserve = int.Parse(config["Ollama:ResponseReserve"] ?? "1024");

    /// <summary>
    /// Estimate token count using chars/4 heuristic (close enough for budget management).
    /// </summary>
    public int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return (int)Math.Ceiling(text.Length / 4.0);
    }

    /// <summary>
    /// Allocate token budget across system prompt, RAG context, history, and response.
    /// </summary>
    public TokenBudget AllocateBudget(string systemPrompt, string userQuery, List<OllamaMessage> history, string ragContext)
    {
        var systemTokens = EstimateTokens(systemPrompt);
        var queryTokens = EstimateTokens(userQuery);
        var ragTokens = EstimateTokens(ragContext);
        var historyTokens = history.Sum(m => EstimateTokens(m.Content));

        var totalUsed = systemTokens + queryTokens + ragTokens + historyTokens;
        var available = _contextWindow - _responseReserve;

        if (totalUsed <= available)
        {
            // Everything fits
            return new TokenBudget(_contextWindow, systemTokens, ragTokens, historyTokens, queryTokens, _responseReserve);
        }

        // Need to trim — allocate proportionally with priorities:
        // 1. System prompt + query are fixed (must include)
        // 2. RAG context gets 50% of remaining
        // 3. History gets the rest
        var fixedTokens = systemTokens + queryTokens;
        var remaining = available - fixedTokens;

        if (remaining <= 0)
        {
            logger.LogWarning("System prompt + query ({Tokens} tokens) exceeds context window budget ({Available} available)", fixedTokens, available);
            return new TokenBudget(_contextWindow, systemTokens, 0, 0, queryTokens, _responseReserve);
        }

        var maxRag = (int)(remaining * 0.55);     // RAG gets priority
        var maxHistory = remaining - maxRag;

        var finalRag = Math.Min(ragTokens, maxRag);
        var finalHistory = Math.Min(historyTokens, maxHistory);

        // If one doesn't use its full allocation, give surplus to the other
        if (finalRag < maxRag)
            finalHistory = Math.Min(historyTokens, maxHistory + (maxRag - finalRag));
        else if (finalHistory < maxHistory)
            finalRag = Math.Min(ragTokens, maxRag + (maxHistory - finalHistory));

        logger.LogInformation(
            "Token budget: system={System} query={Query} rag={Rag}/{RagMax} history={History}/{HistoryMax} reserve={Reserve} total={Total}",
            systemTokens, queryTokens, finalRag, ragTokens, finalHistory, historyTokens, _responseReserve, _contextWindow);

        return new TokenBudget(_contextWindow, systemTokens, finalRag, finalHistory, queryTokens, _responseReserve);
    }

    /// <summary>
    /// Trim conversation history from oldest messages to fit within token budget.
    /// </summary>
    public List<OllamaMessage> TrimHistory(List<OllamaMessage> messages, int maxTokens)
    {
        if (maxTokens <= 0) return [];

        var result = new List<OllamaMessage>();
        var tokenCount = 0;

        // Keep most recent messages, iterate from newest
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var msgTokens = EstimateTokens(messages[i].Content);
            if (tokenCount + msgTokens > maxTokens) break;
            tokenCount += msgTokens;
            result.Insert(0, messages[i]);
        }

        if (result.Count < messages.Count)
            logger.LogInformation("Trimmed history from {Original} to {Trimmed} messages ({Tokens} tokens)",
                messages.Count, result.Count, tokenCount);

        return result;
    }

    /// <summary>
    /// Trim RAG sources from lowest-scored to fit within token budget.
    /// Returns formatted context string.
    /// </summary>
    public string TrimRagContext(List<RagSource> sources, int maxTokens)
    {
        if (maxTokens <= 0 || sources.Count == 0) return "";

        // Sources should already be sorted by score descending
        var included = new List<RagSource>();
        var tokenCount = 0;

        foreach (var source in sources)
        {
            var sourceTokens = EstimateTokens(source.Snippet);
            if (tokenCount + sourceTokens > maxTokens) break;
            tokenCount += sourceTokens;
            included.Add(source);
        }

        if (included.Count < sources.Count)
            logger.LogInformation("Trimmed RAG sources from {Original} to {Trimmed} to fit {Tokens} token budget",
                sources.Count, included.Count, maxTokens);

        return FormatSources(included);
    }

    private static string FormatSources(List<RagSource> sources)
    {
        if (sources.Count == 0) return "";
        return string.Join("\n\n", sources.Select((s, i) =>
            $"[Source {i + 1}: {s.FileName} (relevance: {s.Similarity:F2})]\n{s.Snippet}"));
    }
}
