using LocalAI.Api.Models;

namespace LocalAI.Api.Services.Quality;

public interface IConfidenceScorer
{
    ConfidenceResult Score(string answer, List<RagSource> sources);
}

public class ConfidenceScorer(IConfiguration config) : IConfidenceScorer
{
    private readonly bool _enabled = bool.Parse(config["Quality:EnableConfidenceScoring"] ?? "true");

    public ConfidenceResult Score(string answer, List<RagSource> sources)
    {
        if (!_enabled || sources.Count == 0)
            return new ConfidenceResult(0, 0, 0, "low");

        // Retrieval quality: average similarity of sources used
        var retrievalQuality = sources.Average(s => s.Similarity);

        // Source coverage: what fraction of answer sentences overlap with sources
        var sourceCoverage = ComputeSourceCoverage(answer, sources);

        // Overall score (weighted average)
        var overall = 0.5 * retrievalQuality + 0.5 * sourceCoverage;

        var level = overall switch
        {
            >= 0.7 => "high",
            >= 0.4 => "medium",
            _ => "low"
        };

        return new ConfidenceResult(
            Math.Round(overall, 3),
            Math.Round(retrievalQuality, 3),
            Math.Round(sourceCoverage, 3),
            level
        );
    }

    private static double ComputeSourceCoverage(string answer, List<RagSource> sources)
    {
        // Split answer into sentences
        var answerSentences = answer
            .Split(new[] { ". ", ".\n", "!\n", "?\n", "! ", "? " }, StringSplitOptions.RemoveEmptyEntries)
            .Where(s => s.Length > 10)
            .ToList();

        if (answerSentences.Count == 0) return 0;

        var allSourceText = string.Join(" ", sources.Select(s => s.Snippet.ToLowerInvariant()));

        var coveredCount = 0;
        foreach (var sentence in answerSentences)
        {
            // Extract key words (4+ chars) from sentence
            var keywords = sentence.ToLowerInvariant()
                .Split(new[] { ' ', '\t', '\n', ',', ';', ':', '(', ')', '[', ']' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length >= 4)
                .ToList();

            if (keywords.Count == 0) continue;

            // Check how many keywords appear in source text
            var matchingKeywords = keywords.Count(kw => allSourceText.Contains(kw));
            var overlapRatio = (double)matchingKeywords / keywords.Count;

            if (overlapRatio >= 0.3) // At least 30% keyword overlap = covered
                coveredCount++;
        }

        return (double)coveredCount / answerSentences.Count;
    }
}
