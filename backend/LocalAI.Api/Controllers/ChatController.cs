using LocalAI.Api.Data;
using LocalAI.Api.Models;
using LocalAI.Api.Services.Ollama;
using LocalAI.Api.Services.Rag;
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
    IRagService rag,
    IConfiguration config) : ControllerBase
{
    private readonly string _defaultModel = config["Ollama:ChatModel"] ?? "llama3.2";

    // ── POST /api/chat/stream  (SSE streaming) ───────
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

        // Build RAG context if enabled
        string ragContext = "";
        List<RagSource> sources = [];

        if (request.UseRag)
        {
            sources = await rag.SearchAsync(request.Message, request.RagMode, ct: ct);
            ragContext = await rag.BuildContextAsync(request.Message, request.RagMode, ct);
        }

        // Build message history for Ollama
        var history = (conversation.Messages ?? [])
            .Where(m => m.Id != userMessage.Id)
            .OrderBy(m => m.CreatedAt)
            .TakeLast(10)   // last 10 messages for context window
            .Select(m => new OllamaMessage(m.Role, m.Content))
            .ToList();

        // Inject RAG context into system prompt
        var systemPrompt = ragContext.Length > 0
            ? ragContext
            : "You are a helpful AI assistant running locally on the user's machine.";

        // var messages = new List<OllamaMessage>
        // {
        //     new("system", systemPrompt),
        //     ..history,
        //     new("user", request.Message)
        // };

        var messages = new List<OllamaMessage>();
        messages.Add(new OllamaMessage("system", systemPrompt));
        messages.AddRange(history);
        messages.Add(new OllamaMessage("user", request.Message));

        // Stream tokens via SSE
        var fullResponse = new StringBuilder();

        // First event: metadata (conversationId + sources)
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

        // Save assistant response
        var assistantMessage = new Message
        {
            ConversationId = conversation.Id,
            Role = "assistant",
            Content = fullResponse.ToString(),
            Model = model
        };
        db.Messages.Add(assistantMessage);
        conversation.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);

        // Final done event
        var doneEvent = JsonSerializer.Serialize(new
        {
            type = "done",
            messageId = assistantMessage.Id
        });
        await Response.WriteAsync($"data: {doneEvent}\n\n", ct);
        await Response.Body.FlushAsync(ct);
    }

    // ── GET /api/chat/conversations ──────────────────
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

    // ── GET /api/chat/conversations/{id} ─────────────
    [HttpGet("conversations/{id:guid}")]
    public async Task<IActionResult> GetConversation(Guid id, CancellationToken ct)
    {
        var convo = await db.Conversations
            .Include(c => c.Messages.OrderBy(m => m.CreatedAt))
            .FirstOrDefaultAsync(c => c.Id == id, ct);

        return convo is null ? NotFound() : Ok(convo);
    }

    // ── DELETE /api/chat/conversations/{id} ──────────
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
