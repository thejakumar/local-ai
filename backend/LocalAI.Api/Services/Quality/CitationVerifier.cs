using System.Text.RegularExpressions;
using LocalAI.Api.Models;

namespace LocalAI.Api.Services.Quality;

public interface ICitationVerifier
{
    List<CitationCheck> VerifyCitations(string answer, List<RagSource> sources);
}

public record CitationCheck(string FileName, bool Verified, string? Reason = null);

public class CitationVerifier(IConfiguration config) : ICitationVerifier
{
    private readonly bool _enabled = bool.Parse(config["Quality:EnableCitationVerification"] ?? "true");

    public List<CitationCheck> VerifyCitations(string answer, List<RagSource> sources)
    {
        if (!_enabled) return [];

        // Extract [Source: filename] citations from the answer
        var citationPattern = @"\[Source:\s*(.+?)\]";
        var matches = Regex.Matches(answer, citationPattern);

        if (matches.Count == 0) return [];

        var sourcesByName = sources.ToDictionary(
            s => s.FileName.ToLowerInvariant(),
            s => s.Snippet);

        var checks = new List<CitationCheck>();

        foreach (Match match in matches)
        {
            var citedFile = match.Groups[1].Value.Trim();
            var citedFileLower = citedFile.ToLowerInvariant();

            if (sourcesByName.ContainsKey(citedFileLower))
            {
                checks.Add(new CitationCheck(citedFile, true));
            }
            else
            {
                // Check partial match (file might be cited without extension or path)
                var partialMatch = sourcesByName.Keys.Any(k => k.Contains(citedFileLower) || citedFileLower.Contains(k));
                if (partialMatch)
                    checks.Add(new CitationCheck(citedFile, true, "Partial match"));
                else
                    checks.Add(new CitationCheck(citedFile, false, "Source not found in retrieved documents"));
            }
        }

        return checks;
    }
}
