using LocalAI.Api.Models;
using LocalAI.Api.Services.Ollama;

namespace LocalAI.Api.Services.Context;

public interface IContextCompressionService
{
    Task<string> CompressAsync(string query, string chunkContent, CancellationToken ct = default);
    Task<List<RagSource>> CompressSourcesAsync(string query, List<RagSource> sources, CancellationToken ct = default);
}

public class ContextCompressionService(
    IOllamaService ollama,
    IConfiguration config,
    ILogger<ContextCompressionService> logger) : IContextCompressionService
{
    private readonly bool _enabled = bool.Parse(config["Context:EnableCompression"] ?? "false");
    private readonly int _minChunkLength = int.Parse(config["Context:CompressionMinChunkLength"] ?? "500");

    private const string CompressionPrompt = """
        Extract only the sentences from the following passage that are relevant to answering the question. Return the relevant sentences verbatim, preserving their original wording. If no sentences are relevant, return "NONE".

        Question: {QUERY}

        Passage: {PASSAGE}

        Relevant sentences:
        """;

    public async Task<string> CompressAsync(string query, string chunkContent, CancellationToken ct = default)
    {
        if (!_enabled || chunkContent.Length < _minChunkLength)
            return chunkContent;

        var prompt = CompressionPrompt
            .Replace("{QUERY}", query)
            .Replace("{PASSAGE}", chunkContent);

        try
        {
            var result = await ollama.ChatAsync(
                [new OllamaMessage("user", prompt)],
                temperature: 0.0,
                maxTokens: 512,
                ct: ct);

            if (result.Trim().Equals("NONE", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation("Context compression: chunk deemed irrelevant");
                return "";
            }

            // Only use compressed version if it's actually shorter
            if (result.Length < chunkContent.Length)
            {
                logger.LogInformation("Compressed chunk from {Original} to {Compressed} chars",
                    chunkContent.Length, result.Length);
                return result;
            }

            return chunkContent;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Context compression failed, using original chunk");
            return chunkContent;
        }
    }

    public async Task<List<RagSource>> CompressSourcesAsync(
        string query, List<RagSource> sources, CancellationToken ct = default)
    {
        if (!_enabled) return sources;

        var compressed = new List<RagSource>();
        foreach (var source in sources)
        {
            var compressedContent = await CompressAsync(query, source.Snippet, ct);
            if (!string.IsNullOrWhiteSpace(compressedContent))
            {
                compressed.Add(new RagSource(
                    source.FileName,
                    compressedContent,
                    source.Similarity,
                    source.SearchType
                ));
            }
        }

        return compressed;
    }
}
