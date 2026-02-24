using LocalAI.Api.Models;
using System.Net.Http.Json;

namespace LocalAI.Api.Services.Embedding;

public interface IEmbeddingService
{
    Task<float[]> GetEmbeddingAsync(string text, CancellationToken ct = default);
    Task<List<float[]>> GetBatchEmbeddingsAsync(List<string> texts, CancellationToken ct = default);
}

public class EmbeddingService(
    IHttpClientFactory httpFactory,
    IConfiguration config,
    ILogger<EmbeddingService> logger) : IEmbeddingService
{
    private readonly string _model = config["Ollama:EmbeddingModel"] ?? "nomic-embed-text";
    private readonly int _batchConcurrency = int.Parse(config["Embedding:BatchConcurrency"] ?? "3");
    private const int MaxRetries = 1;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);

    public async Task<float[]> GetEmbeddingAsync(string text, CancellationToken ct = default)
    {
        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            try
            {
                var http = httpFactory.CreateClient("ollama");
                var request = new OllamaEmbedRequest(_model, text);
                var response = await http.PostAsJsonAsync("/api/embeddings", request, ct);

                if (!response.IsSuccessStatusCode)
                {
                    var err = await response.Content.ReadAsStringAsync(ct);
                    logger.LogError("Embedding failed (attempt {Attempt}): {Error}", attempt + 1, err);
                    if (attempt < MaxRetries)
                    {
                        await Task.Delay(RetryDelay, ct);
                        continue;
                    }
                    throw new InvalidOperationException($"Embedding failed after {MaxRetries + 1} attempts: {err}");
                }

                var result = await response.Content.ReadFromJsonAsync<OllamaEmbedResponse>(ct);

                if (result?.Embedding == null || result.Embedding.Length == 0)
                {
                    logger.LogError("Embedding returned empty for text: '{Preview}...' (attempt {Attempt})",
                        text[..Math.Min(50, text.Length)], attempt + 1);
                    if (attempt < MaxRetries)
                    {
                        await Task.Delay(RetryDelay, ct);
                        continue;
                    }
                    throw new InvalidOperationException(
                        $"Embedding returned empty after {MaxRetries + 1} attempts for text: '{text[..Math.Min(50, text.Length)]}...'");
                }

                return result.Embedding;
            }
            catch (Exception ex) when (attempt < MaxRetries && ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Embedding attempt {Attempt} failed, retrying...", attempt + 1);
                await Task.Delay(RetryDelay, ct);
            }
        }

        throw new InvalidOperationException("Embedding failed — all retries exhausted");
    }

    public async Task<List<float[]>> GetBatchEmbeddingsAsync(List<string> texts, CancellationToken ct = default)
    {
        if (texts.Count == 0) return [];

        logger.LogInformation("Getting batch embeddings for {Count} texts with concurrency={Concurrency}",
            texts.Count, _batchConcurrency);

        var results = new float[texts.Count][];
        var semaphore = new SemaphoreSlim(_batchConcurrency);
        var failures = new List<int>();

        var tasks = texts.Select(async (text, index) =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                results[index] = await GetEmbeddingAsync(text, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to embed chunk {Index}: '{Preview}...'",
                    index, text[..Math.Min(30, text.Length)]);
                results[index] = [];  // Mark as failed — caller should check
                lock (failures) failures.Add(index);
            }
            finally
            {
                semaphore.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks);

        if (failures.Count > 0)
            logger.LogWarning("Batch embedding completed with {Failures} failures out of {Total}",
                failures.Count, texts.Count);
        else
            logger.LogInformation("Completed batch embeddings for {Count} texts", texts.Count);

        return results.ToList();
    }
}
