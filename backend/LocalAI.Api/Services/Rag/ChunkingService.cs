using System.Text.RegularExpressions;

namespace LocalAI.Api.Services.Rag;

/// <summary>
/// Intelligent document chunking that respects document structure.
/// Handles code, markdown, and plain text differently.
/// </summary>
public interface IChunkingService
{
    List<string> ChunkText(string text, string fileType);
}

public class ChunkingService : IChunkingService
{
    private readonly int _defaultChunkSize = 512;
    private readonly int _defaultChunkOverlap = 64;
    private readonly int _codeChunkSize = 300;  // Smaller for code to preserve context
    private readonly int _codeChunkOverlap = 50;
    private readonly int _markdownChunkSize = 400;

    /// <summary>
    /// Smart chunking that respects document structure based on file type.
    /// </summary>
    public List<string> ChunkText(string text, string fileType)
    {
        return fileType.ToLowerInvariant() switch
        {
            "code" => ChunkCodeFile(text),
            "markdown" => ChunkMarkdownFile(text),
            "pdf" => ChunkPdfText(text),
            _ => ChunkPlainText(text)
        };
    }

    /// <summary>
    /// Chunk code files by function/class boundaries when possible,
    /// fall back to token-based chunking otherwise.
    /// </summary>
    private List<string> ChunkCodeFile(string text)
    {
        var chunks = new List<string>();

        // Try to split by function/class definitions (for C#, Java, Python, JS, etc.)
        var functionPattern = @"(?:^|\n)(?:public|private|protected|async\s+)?(?:class|interface|enum|void|async\s+\w+|\w+)\s+\w+\s*\(";
        var functions = Regex.Split(text, functionPattern, RegexOptions.Multiline);

        if (functions.Length > 1)
        {
            // We found function boundaries - chunk by functions
            foreach (var func in functions.Where(f => !string.IsNullOrWhiteSpace(f)))
            {
                if (func.Length > _codeChunkSize * 3)
                {
                    // Function is too large, apply token-based chunking
                    chunks.AddRange(ChunkByTokens(func, _codeChunkSize, _codeChunkOverlap));
                }
                else if (func.Length > 50)
                {
                    chunks.Add(func.Trim());
                }
            }
        }
        else
        {
            // No clear function boundaries, fall back to token-based
            chunks.AddRange(ChunkByTokens(text, _codeChunkSize, _codeChunkOverlap));
        }

        return chunks.Where(c => c.Length > 10).ToList();
    }

    /// <summary>
    /// Chunk markdown by headers and preserve structure.
    /// </summary>
    private List<string> ChunkMarkdownFile(string text)
    {
        var chunks = new List<string>();
        var lines = text.Split('\n');
        var currentChunk = new List<string>();
        int headerLevel = int.MaxValue;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            
            // Detect markdown headers
            var headerMatch = Regex.Match(line, @"^(#+)\s");
            if (headerMatch.Success)
            {
                int currentHeaderLevel = headerMatch.Groups[1].Length;

                // If we hit a header at same or higher level, save current chunk
                if (currentChunk.Count > 0 && currentHeaderLevel <= headerLevel)
                {
                    var chunkText = string.Join("\n", currentChunk).Trim();
                    if (chunkText.Length > 20)
                        chunks.Add(chunkText);
                    currentChunk.Clear();
                }

                headerLevel = currentHeaderLevel;
            }

            currentChunk.Add(line);

            // Also chunk if we exceed size
            if (string.Join("\n", currentChunk).Length > _markdownChunkSize)
            {
                var chunkText = string.Join("\n", currentChunk).Trim();
                if (chunkText.Length > 20)
                    chunks.Add(chunkText);
                currentChunk.Clear();
            }
        }

        // Don't forget the last chunk
        if (currentChunk.Count > 0)
        {
            var chunkText = string.Join("\n", currentChunk).Trim();
            if (chunkText.Length > 20)
                chunks.Add(chunkText);
        }

        return chunks;
    }

    /// <summary>
    /// PDF chunks - preserve logical boundaries (paragraphs, sections)
    /// </summary>
    private List<string> ChunkPdfText(string text)
    {
        var chunks = new List<string>();

        // Split by double newlines (paragraph boundaries) first
        var paragraphs = text.Split(new[] { "\n\n", "\r\n\r\n" }, StringSplitOptions.RemoveEmptyEntries);

        var currentChunk = new List<string>();
        int currentSize = 0;

        foreach (var para in paragraphs)
        {
            if (currentSize + para.Length > _defaultChunkSize && currentChunk.Count > 0)
            {
                // Flush current chunk
                chunks.Add(string.Join("\n\n", currentChunk).Trim());
                currentChunk.Clear();
                currentSize = 0;
            }

            currentChunk.Add(para);
            currentSize += para.Length + 2; // +2 for newlines
        }

        if (currentChunk.Count > 0)
        {
            chunks.Add(string.Join("\n\n", currentChunk).Trim());
        }

        // Apply token-based chunking to any oversized chunks
        return chunks.SelectMany(c => c.Length > _defaultChunkSize * 2
            ? ChunkByTokens(c, _defaultChunkSize, _defaultChunkOverlap)
            : new List<string> { c }
        ).Where(c => c.Length > 10).ToList();
    }

    /// <summary>
    /// Plain text chunking by word/token boundaries with overlap.
    /// </summary>
    private List<string> ChunkPlainText(string text)
    {
        return ChunkByTokens(text, _defaultChunkSize, _defaultChunkOverlap);
    }

    /// <summary>
    /// Token-based chunking by word boundaries.
    /// </summary>
    private List<string> ChunkByTokens(string text, int chunkSize, int chunkOverlap)
    {
        var chunks = new List<string>();

        // Split by whitespace, but preserve word integrity
        var tokens = text.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length == 0) return chunks;

        int i = 0;
        while (i < tokens.Length)
        {
            var chunkTokens = tokens.Skip(i).Take(chunkSize).ToList();
            if (chunkTokens.Count > 0)
            {
                chunks.Add(string.Join(" ", chunkTokens));
            }

            i += chunkSize - chunkOverlap;
        }

        return chunks;
    }
}
