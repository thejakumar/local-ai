using LocalAI.Api.Models;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace LocalAI.Api.Services.Ollama;

public interface IOllamaService
{
    IAsyncEnumerable<string> StreamChatAsync(
        List<OllamaMessage> messages,
        string? model = null,
        CancellationToken ct = default);

    Task<string> ChatAsync(
        List<OllamaMessage> messages,
        string? model = null,
        CancellationToken ct = default);
}

public class OllamaService(
    IHttpClientFactory httpFactory,
    IConfiguration config,
    ILogger<OllamaService> logger) : IOllamaService
{
    private readonly string _chatModel      = config["Ollama:ChatModel"] ?? "llama3.2";
    private readonly double _temperature    = double.Parse(config["Ollama:Temperature"] ?? "0.7");
    private readonly int    _maxTokens      = int.Parse(config["Ollama:MaxTokens"] ?? "4096");

    // ── Non-streaming (for short completions) ────────
    public async Task<string> ChatAsync(
        List<OllamaMessage> messages,
        string? model = null,
        CancellationToken ct = default)
    {
        var http = httpFactory.CreateClient("ollama");
        var request = BuildRequest(messages, model, stream: false);

        var response = await http.PostAsJsonAsync("/api/chat", request, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return body.GetProperty("message").GetProperty("content").GetString() ?? "";
    }

    // ── Streaming chat ────────────────────────────────
    public async IAsyncEnumerable<string> StreamChatAsync(
        List<OllamaMessage> messages,
        string? model = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var http = httpFactory.CreateClient("ollama");
        var request = BuildRequest(messages, model, stream: true);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
        {
            Content = JsonContent.Create(request)
        };

        using var response = await http.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            ct);

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line)) continue;

            OllamaChatChunk? chunk = null;
            try
            {
                chunk = JsonSerializer.Deserialize<OllamaChatChunk>(line);
            }
            catch (JsonException ex)
            {
                logger.LogWarning("Failed to parse chunk: {Error}", ex.Message);
                continue;
            }

            if (chunk is null) continue;
            if (chunk.Done) yield break;

            var token = chunk.MessageChunk?.Content;
            if (!string.IsNullOrEmpty(token))
                yield return token;
        }
    }

    // ── Helpers ───────────────────────────────────────
    private OllamaChatRequest BuildRequest(
        List<OllamaMessage> messages,
        string? model,
        bool stream) => new(
            Model: model ?? _chatModel,
            Messages: messages,
            Stream: stream,
            Options: new(_temperature, _maxTokens)
        );
}
