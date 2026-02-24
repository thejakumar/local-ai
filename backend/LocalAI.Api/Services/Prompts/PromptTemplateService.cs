using LocalAI.Api.Models;

namespace LocalAI.Api.Services.Prompts;

public interface IPromptTemplateService
{
    string BuildSystemPrompt(string? ragContext, string query);
    string DetectQueryType(string query);
}

public class PromptTemplateService : IPromptTemplateService
{
    private const string BaseInstructions = """
        You are a knowledgeable AI assistant running locally. You provide accurate, helpful answers.
        """;

    private const string FactualTemplate = """
        You are a knowledgeable AI assistant running locally. You provide accurate, well-sourced answers.

        INSTRUCTIONS:
        - Answer the question using the provided sources as your primary reference
        - For each key claim, cite the source: [Source: filename]
        - If the sources do not contain enough information, clearly state: "This is not covered in the provided documents" and then offer what general knowledge you have
        - Be thorough but concise
        - Never invent information that isn't in the sources
        - If multiple sources provide conflicting information, mention the discrepancy

        {CONTEXT}
        """;

    private const string CodeTemplate = """
        You are an expert code assistant running locally. You help with programming questions using the provided code context.

        INSTRUCTIONS:
        - Answer using the provided code snippets as reference
        - Include relevant code examples from the sources when helpful
        - Explain the code's purpose, patterns, and how it relates to the question
        - Cite which file the code comes from: [Source: filename]
        - If the provided code doesn't cover the question, say so and offer general programming guidance
        - Use proper code formatting with language-specific syntax highlighting

        {CONTEXT}
        """;

    private const string SummaryTemplate = """
        You are a knowledgeable AI assistant running locally. You provide clear, organized summaries.

        INSTRUCTIONS:
        - Summarize the key information from the sources that relates to the question
        - Organize your response with clear structure (bullet points, sections)
        - Cite sources for key points: [Source: filename]
        - Highlight the most important findings first
        - If the sources provide incomplete coverage, note what areas are not covered

        {CONTEXT}
        """;

    private const string DefaultNoRagTemplate = """
        You are a helpful AI assistant running locally on the user's machine. You provide accurate, thoughtful answers based on your training knowledge. Be concise but thorough.
        """;

    /// <summary>
    /// Build the system prompt combining behavioral instructions with RAG context.
    /// </summary>
    public string BuildSystemPrompt(string? ragContext, string query)
    {
        if (string.IsNullOrWhiteSpace(ragContext))
            return DefaultNoRagTemplate;

        var queryType = DetectQueryType(query);
        var template = queryType switch
        {
            "code" => CodeTemplate,
            "summary" => SummaryTemplate,
            _ => FactualTemplate
        };

        var contextBlock = $"""

            RELEVANT SOURCES FROM YOUR KNOWLEDGE BASE:
            {ragContext}
            """;

        return template.Replace("{CONTEXT}", contextBlock);
    }

    /// <summary>
    /// Detect query type using keyword heuristics.
    /// </summary>
    public string DetectQueryType(string query)
    {
        var lower = query.ToLowerInvariant();

        // Code-related detection
        var codeKeywords = new[] { "code", "function", "class", "method", "implement", "bug", "error",
            "compile", "syntax", "debug", "refactor", "api", "endpoint", "variable", "type",
            ".cs", ".ts", ".js", ".py", ".java", "async", "await", "interface", "exception" };
        if (codeKeywords.Any(k => lower.Contains(k)))
            return "code";

        // Summary detection
        var summaryKeywords = new[] { "summarize", "summary", "overview", "what are", "list all",
            "give me a", "tell me about", "explain all", "describe the" };
        if (summaryKeywords.Any(k => lower.Contains(k)))
            return "summary";

        return "factual";
    }
}
