using LocalAI.Api.Models;
using LocalAI.Api.Services.Ollama;

namespace LocalAI.Api.Services.Retrieval;

public interface IHydeService
{
    Task<string> GenerateHypotheticalDocumentAsync(string query, CancellationToken ct = default);
}

public class HydeService(
    IOllamaService ollama,
    IConfiguration config,
    ILogger<HydeService> logger) : IHydeService
{
    private readonly double _temperature = double.Parse(config["Retrieval:HydeTemperature"] ?? "0.1");

    private const string HydePrompt = """
        Write a detailed, factual paragraph that directly answers the following question. Do not include any preamble, qualifiers, or meta-commentary. Write the answer as if it were excerpted from a technical document or knowledge base.

        Question: {QUERY}
        """;

    public async Task<string> GenerateHypotheticalDocumentAsync(string query, CancellationToken ct = default)
    {
        var prompt = HydePrompt.Replace("{QUERY}", query);
        var messages = new List<OllamaMessage>
        {
            new("user", prompt)
        };

        try
        {
            var result = await ollama.ChatAsync(messages, temperature: _temperature, maxTokens: 512, ct: ct);
            logger.LogInformation("HyDE generated {Length} char hypothetical document for query: '{Query}'",
                result.Length, query[..Math.Min(50, query.Length)]);
            return result;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "HyDE generation failed, falling back to original query");
            return query;
        }
    }
}
