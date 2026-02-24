using LocalAI.Api.Data;
using LocalAI.Api.Models;
using LocalAI.Api.Services.Context;
using LocalAI.Api.Services.Ollama;
using LocalAI.Api.Services.Prompts;
using LocalAI.Api.Services.Quality;
using LocalAI.Api.Services.Retrieval;
using LocalAI.Api.Services.Token;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text;
using System.Text.Json;

namespace LocalAI.Api.Controllers;

[ApiController]
[Route("api/chat")]
public class ChatController(
    AppDbContext db,
    IOllamaService ollama,
    IRetrievalPipeline retrievalPipeline,
    IPromptTemplateService promptTemplate,
    ITokenBudgetService tokenBudget,
    IConversationSummaryService conversationSummary,
    IContextCompressionService contextCompression,
    IConfidenceScorer confidenceScorer,
    ICitationVerifier citationVerifier,
    IConfiguration config) : ControllerBase
{
    private readonly string _defaultModel = config["Ollama:ChatModel"] ?? "llama3.2";

    [HttpPost("stream")]
    public async Task StreamChat([FromBody] ChatRequest request, CancellationToken ct)
    {
        Response.Headers.Append("Content-Type", "text/event-stream");
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("X-Accel-Buffering", "no");

        var model = request.Model ?? _defaultModel;

        // Get or create conversation
        var conversation = request.ConversationId.HasValue
            ? await db.Conversations
                .Include(c => c.Messages)
                .FirstOrDefaultAsync(c => c.Id == request.ConversationId, ct)
            : null;

        conversation ??= new Conversation
        {
            Title = request.Message.Length > 50
                ? request.Message[..50] + "..."
                : request.Message
        };

        if (!db.Conversations.Local.Contains(conversation))
            db.Conversations.Add(conversation);

        // Save user message
        var userMessage = new Message
        {
            ConversationId = conversation.Id,
            Role = "user",
            Content = request.Message
        };
        db.Messages.Add(userMessage);

        // Build RAG context using advanced retrieval pipeline
        List<RagSource> sources = [];
        string ragContext = "";

        if (request.UseRag)
        {
            var retrievalOptions = new RetrievalOptions(
                SearchMode: request.RagMode ?? "hybrid",
                TopK: 5,
                EnableQueryExpansion: true,
                EnableHyde: true,
                EnableReranking: true,
                FileTypeFilter: request.FileTypeFilter,
                FileNameFilter: request.FileNameFilter
            );

            sources = await retrievalPipeline.RetrieveAsync(request.Message, retrievalOptions, ct);

            // Context compression (if enabled)
            sources = await contextCompression.CompressSourcesAsync(request.Message, sources, ct);

            ragContext = string.Join("\n\n", sources.Select((s, i) =>
                $"[Source {i + 1}: {s.FileName} (relevance: {s.Similarity:F2})]\n{s.Snippet}"));
        }

        // Build system prompt with behavioral instructions + RAG context
        var systemPrompt = promptTemplate.BuildSystemPrompt(
            string.IsNullOrWhiteSpace(ragContext) ? null : ragContext,
            request.Message);

        // Get conversation summary for long chats
        string? summary = null;
        if (conversationSummary.NeedsSummarization(conversation))
        {
            summary = await conversationSummary.GetOrCreateSummaryAsync(conversation, ct);
        }

        // Build message history
        var history = (conversation.Messages ?? [])
            .Where(m => m.Id != userMessage.Id)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new OllamaMessage(m.Role, m.Content))
            .ToList();

        // Apply token budget management
        var budget = tokenBudget.AllocateBudget(systemPrompt, request.Message, history, ragContext);
        history = tokenBudget.TrimHistory(history, budget.HistoryTokens);

        // Build final messages list
        var messages = new List<OllamaMessage>();

        if (summary != null)
            messages.Add(new OllamaMessage("system", systemPrompt + $"\n\nPrevious conversation context: {summary}"));
        else
            messages.Add(new OllamaMessage("system", systemPrompt));

        messages.AddRange(history);
        messages.Add(new OllamaMessage("user", request.Message));

        // Stream tokens via SSE
        var fullResponse = new StringBuilder();

        // First event: metadata
        var meta = JsonSerializer.Serialize(new
        {
            type = "meta",
            conversationId = conversation.Id,
            sources
        });
        await Response.WriteAsync($"data: {meta}\n\n", ct);
        await Response.Body.FlushAsync(ct);

        await foreach (var token in ollama.StreamChatAsync(messages, model, ct))
        {
            fullResponse.Append(token);

            var tokenEvent = JsonSerializer.Serialize(new
            {
                type = "token",
                content = token
            });
            await Response.WriteAsync($"data: {tokenEvent}\n\n", ct);
            await Response.Body.FlushAsync(ct);
        }

        var responseText = fullResponse.ToString();

        // Compute confidence and verify citations
        var confidence = confidenceScorer.Score(responseText, sources);
        var citations = citationVerifier.VerifyCitations(responseText, sources);

        // Save assistant response
        var assistantMessage = new Message
        {
            ConversationId = conversation.Id,
            Role = "assistant",
            Content = responseText,
            Model = model,
            TokensUsed = tokenBudget.EstimateTokens(responseText)
        };
        db.Messages.Add(assistantMessage);
        conversation.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);

        // Final done event with quality metadata
        var doneEvent = JsonSerializer.Serialize(new
        {
            type = "done",
            messageId = assistantMessage.Id,
            confidence = new { confidence.Overall, confidence.Level, confidence.RetrievalQuality, confidence.SourceCoverage },
            citations,
            tokensUsed = assistantMessage.TokensUsed
        });
        await Response.WriteAsync($"data: {doneEvent}\n\n", ct);
        await Response.Body.FlushAsync(ct);
    }

    [HttpGet("conversations")]
    public async Task<IActionResult> GetConversations(CancellationToken ct)
    {
        var convos = await db.Conversations
            .Include(c => c.Messages)
            .OrderByDescending(c => c.UpdatedAt)
            .Select(c => new ConversationSummary(
                c.Id,
                c.Title,
                c.UpdatedAt,
                c.Messages.Count
            ))
            .ToListAsync(ct);

        return Ok(convos);
    }

    [HttpGet("conversations/{id:guid}")]
    public async Task<IActionResult> GetConversation(Guid id, CancellationToken ct)
    {
        var convo = await db.Conversations
            .Include(c => c.Messages.OrderBy(m => m.CreatedAt))
            .FirstOrDefaultAsync(c => c.Id == id, ct);

        return convo is null ? NotFound() : Ok(convo);
    }

    [HttpDelete("conversations/{id:guid}")]
    public async Task<IActionResult> DeleteConversation(Guid id, CancellationToken ct)
    {
        var convo = await db.Conversations.FindAsync([id], ct);
        if (convo is null) return NotFound();
        db.Conversations.Remove(convo);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }
}
