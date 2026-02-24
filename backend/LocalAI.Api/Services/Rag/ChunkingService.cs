using System.Text.RegularExpressions;

namespace LocalAI.Api.Services.Rag;

public interface IChunkingService
{
    List<string> ChunkText(string text, string fileType, int? chunkSize = null, int? chunkOverlap = null);
    List<string> ChunkByCharacters(string text, int chunkSize, int chunkOverlap);
}

public class ChunkingService : IChunkingService
{
    // Default sizes in CHARACTERS (not tokens/words)
    private readonly int _defaultChunkSize = 1500;     // ~375 tokens
    private readonly int _defaultChunkOverlap = 200;   // ~50 tokens
    private readonly int _codeChunkSize = 1200;
    private readonly int _codeChunkOverlap = 150;
    private readonly int _markdownChunkSize = 1500;

    public List<string> ChunkText(string text, string fileType, int? chunkSize = null, int? chunkOverlap = null)
    {
        return fileType.ToLowerInvariant() switch
        {
            "code" => ChunkCodeFile(text, chunkSize ?? _codeChunkSize, chunkOverlap ?? _codeChunkOverlap),
            "markdown" => ChunkMarkdownFile(text, chunkSize ?? _markdownChunkSize),
            "pdf" => ChunkPdfText(text, chunkSize ?? _defaultChunkSize, chunkOverlap ?? _defaultChunkOverlap),
            _ => ChunkByCharacters(text, chunkSize ?? _defaultChunkSize, chunkOverlap ?? _defaultChunkOverlap)
        };
    }

    private List<string> ChunkCodeFile(string text, int chunkSize, int chunkOverlap)
    {
        var chunks = new List<string>();

        var functionPattern = @"(?:^|\n)(?:public|private|protected|internal|static|async\s+)?(?:class|interface|enum|record|struct|void|async\s+\w+|\w+)\s+\w+\s*[\(<{]";
        var functions = Regex.Split(text, functionPattern, RegexOptions.Multiline);

        if (functions.Length > 1)
        {
            foreach (var func in functions.Where(f => !string.IsNullOrWhiteSpace(f)))
            {
                if (func.Length > chunkSize * 3)
                    chunks.AddRange(ChunkByCharacters(func, chunkSize, chunkOverlap));
                else if (func.Length > 30)
                    chunks.Add(func.Trim());
            }
        }
        else
        {
            chunks.AddRange(ChunkByCharacters(text, chunkSize, chunkOverlap));
        }

        return chunks.Where(c => c.Length > 20).ToList();
    }

    private List<string> ChunkMarkdownFile(string text, int chunkSize)
    {
        var chunks = new List<string>();
        var lines = text.Split('\n');
        var currentChunk = new List<string>();
        int headerLevel = int.MaxValue;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var headerMatch = Regex.Match(line, @"^(#+)\s");
            if (headerMatch.Success)
            {
                int currentHeaderLevel = headerMatch.Groups[1].Length;
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

            if (string.Join("\n", currentChunk).Length > chunkSize)
            {
                var chunkText = string.Join("\n", currentChunk).Trim();
                if (chunkText.Length > 20)
                    chunks.Add(chunkText);
                currentChunk.Clear();
            }
        }

        if (currentChunk.Count > 0)
        {
            var chunkText = string.Join("\n", currentChunk).Trim();
            if (chunkText.Length > 20)
                chunks.Add(chunkText);
        }

        return chunks;
    }

    private List<string> ChunkPdfText(string text, int chunkSize, int chunkOverlap)
    {
        var chunks = new List<string>();
        var paragraphs = text.Split(new[] { "\n\n", "\r\n\r\n" }, StringSplitOptions.RemoveEmptyEntries);

        var currentChunk = new List<string>();
        int currentSize = 0;

        foreach (var para in paragraphs)
        {
            if (currentSize + para.Length > chunkSize && currentChunk.Count > 0)
            {
                chunks.Add(string.Join("\n\n", currentChunk).Trim());
                currentChunk.Clear();
                currentSize = 0;
            }
            currentChunk.Add(para);
            currentSize += para.Length + 2;
        }

        if (currentChunk.Count > 0)
            chunks.Add(string.Join("\n\n", currentChunk).Trim());

        return chunks.SelectMany(c => c.Length > chunkSize * 2
            ? ChunkByCharacters(c, chunkSize, chunkOverlap)
            : [c]
        ).Where(c => c.Length > 20).ToList();
    }

    /// <summary>
    /// Character-based chunking that splits at sentence boundaries with overlap.
    /// </summary>
    public List<string> ChunkByCharacters(string text, int chunkSize, int chunkOverlap)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        if (text.Length <= chunkSize) return [text.Trim()];

        var chunks = new List<string>();
        var sentences = SplitIntoSentences(text);

        var currentChunk = new List<string>();
        int currentLength = 0;

        foreach (var sentence in sentences)
        {
            if (currentLength + sentence.Length > chunkSize && currentChunk.Count > 0)
            {
                chunks.Add(string.Join("", currentChunk).Trim());

                // Build overlap from trailing sentences
                var overlapChunk = new List<string>();
                var overlapLength = 0;
                for (int i = currentChunk.Count - 1; i >= 0; i--)
                {
                    if (overlapLength + currentChunk[i].Length > chunkOverlap) break;
                    overlapLength += currentChunk[i].Length;
                    overlapChunk.Insert(0, currentChunk[i]);
                }

                currentChunk = overlapChunk;
                currentLength = overlapLength;
            }

            currentChunk.Add(sentence);
            currentLength += sentence.Length;
        }

        if (currentChunk.Count > 0)
        {
            var final = string.Join("", currentChunk).Trim();
            if (final.Length > 20)
                chunks.Add(final);
        }

        return chunks;
    }

    private static List<string> SplitIntoSentences(string text)
    {
        var sentences = new List<string>();
        var pattern = @"(?<=[.!?])\s+|(?<=\n)\n+";
        var parts = Regex.Split(text, pattern);

        foreach (var part in parts)
        {
            if (!string.IsNullOrEmpty(part))
                sentences.Add(part + " ");
        }

        return sentences;
    }
}
