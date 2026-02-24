using LocalAI.Api.Models;
using LocalAI.Api.Services.Ollama;

namespace LocalAI.Api.Services.Retrieval;

public interface IQueryExpansionService
{
    Task<List<string>> ExpandQueryAsync(string query, CancellationToken ct = default);
}

public class QueryExpansionService(
    IOllamaService ollama,
    IConfiguration config,
    ILogger<QueryExpansionService> logger) : IQueryExpansionService
{
    private readonly int _expansionCount = int.Parse(config["Retrieval:ExpansionCount"] ?? "3");
    private readonly double _temperature = double.Parse(config["Retrieval:ExpansionTemperature"] ?? "0.3");

    private const string ExpansionPrompt = """
        Given the user's question, generate {COUNT} alternative search queries that would help find relevant information. Each query should use different keywords and phrasing to capture various aspects of the question.
        Return ONLY the queries, one per line, no numbering or explanation.

        User question: {QUERY}
        """;

    public async Task<List<string>> ExpandQueryAsync(string query, CancellationToken ct = default)
    {
        var prompt = ExpansionPrompt
            .Replace("{COUNT}", _expansionCount.ToString())
            .Replace("{QUERY}", query);

        var messages = new List<OllamaMessage>
        {
            new("user", prompt)
        };

        try
        {
            var result = await ollama.ChatAsync(messages, temperature: _temperature, maxTokens: 256, ct: ct);

            var queries = result
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(q => q.Length > 5 && !q.StartsWith('#'))
                .Select(q => q.TrimStart('-', '*', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '.', ' '))
                .Where(q => q.Length > 5)
                .Take(_expansionCount)
                .ToList();

            logger.LogInformation("Query expansion generated {Count} alternatives for: '{Query}'",
                queries.Count, query[..Math.Min(50, query.Length)]);

            return queries;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Query expansion failed, using original query only");
            return [];
        }
    }
}
