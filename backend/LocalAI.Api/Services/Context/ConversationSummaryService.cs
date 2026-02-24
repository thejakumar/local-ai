using LocalAI.Api.Models;
using LocalAI.Api.Services.Ollama;

namespace LocalAI.Api.Services.Context;

public interface IConversationSummaryService
{
    Task<string?> GetOrCreateSummaryAsync(Conversation conversation, CancellationToken ct = default);
    bool NeedsSummarization(Conversation conversation);
}

public class ConversationSummaryService(
    IOllamaService ollama,
    IConfiguration config,
    ILogger<ConversationSummaryService> logger) : IConversationSummaryService
{
    private readonly int _summarizeAfter = int.Parse(config["Context:SummarizeAfterMessages"] ?? "8");
    private readonly bool _enabled = bool.Parse(config["Context:EnableSummarization"] ?? "true");

    private const string SummaryPrompt = """
        Summarize the following conversation in 2-3 concise sentences. Preserve key facts, decisions, topics discussed, and any specific technical details mentioned. Focus on what would be important context for continuing this conversation.

        Conversation:
        {MESSAGES}

        Summary:
        """;

    public bool NeedsSummarization(Conversation conversation)
    {
        if (!_enabled) return false;
        var messageCount = conversation.Messages?.Count ?? 0;
        return messageCount > _summarizeAfter;
    }

    public async Task<string?> GetOrCreateSummaryAsync(Conversation conversation, CancellationToken ct = default)
    {
        if (!_enabled) return null;

        var messages = conversation.Messages?
            .OrderBy(m => m.CreatedAt)
            .ToList() ?? [];

        if (messages.Count <= _summarizeAfter)
            return conversation.Summary;

        // Check if summary needs refresh (every 5 new messages after threshold)
        var messagesSinceSummary = messages.Count - _summarizeAfter;
        var needsRefresh = conversation.Summary == null || messagesSinceSummary % 5 == 0;

        if (!needsRefresh) return conversation.Summary;

        // Summarize older messages (everything except last 4)
        var olderMessages = messages.Take(messages.Count - 4).ToList();
        if (olderMessages.Count == 0) return conversation.Summary;

        var messageText = string.Join("\n",
            olderMessages.Select(m => $"{m.Role}: {m.Content[..Math.Min(200, m.Content.Length)]}"));

        var prompt = SummaryPrompt.Replace("{MESSAGES}", messageText);

        try
        {
            var summary = await ollama.ChatAsync(
                [new OllamaMessage("user", prompt)],
                temperature: 0.2,
                maxTokens: 256,
                ct: ct);

            logger.LogInformation("Generated conversation summary ({Length} chars) for {MsgCount} messages",
                summary.Length, olderMessages.Count);

            conversation.Summary = summary;
            return summary;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to generate conversation summary");
            return conversation.Summary;
        }
    }
}
