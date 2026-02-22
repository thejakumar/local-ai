using LocalAI.Api.Models;
using System.Net.Http.Json;

namespace LocalAI.Api.Services.Embedding;

public interface IEmbeddingService
{
    Task<float[]> GetEmbeddingAsync(string text, CancellationToken ct = default);
}

public class EmbeddingService(
    IHttpClientFactory httpFactory,
    IConfiguration config,
    ILogger<EmbeddingService> logger) : IEmbeddingService
{
    private readonly string _model = config["Ollama:EmbeddingModel"] ?? "nomic-embed-text";

    public async Task<float[]> GetEmbeddingAsync(string text, CancellationToken ct = default)
    {
        var http = httpFactory.CreateClient("ollama");

        var request = new OllamaEmbedRequest(_model, text);
        var response = await http.PostAsJsonAsync("/api/embeddings", request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            logger.LogError("Embedding failed: {Error}", err);
            throw new InvalidOperationException($"Embedding failed: {err}");
        }

        var result = await response.Content.ReadFromJsonAsync<OllamaEmbedResponse>(ct);
        return result?.Embedding ?? [];
    }
}
