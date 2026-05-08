using System.Text;
using System.Text.RegularExpressions;
using TestAIPoc.Infrastructure;
using TestAIPoc.Models;

namespace TestAIPoc.Analysis;

public sealed class RepositoryAnalyzer
{
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", "node_modules", ".vs", ".idea", "dist", "publish"
    };

    private static readonly string[] FileExtensionsOfInterest =
    [
        ".cs", ".json", ".yml", ".yaml", ".xml", ".md", ".txt", ".razor", ".ts", ".js", ".tsx", ".jsx", ".sql"
    ];

    private static readonly string[] RuleKeywords =
    [
        "must", "should", "required", "cannot", "can't", "valid", "validation", "reject", "allow", "only", "unless", "if", "when", "authorize"
    ];

    private readonly CSharpSymbolExtractor _symbolExtractor = new();

    public RepositoryAnalysis Analyze(RepositoryWorkspace workspace, string prompt, int maxFiles)
    {
        var terms = TextTermExtractor.ExtractTerms(prompt);
        if (!string.IsNullOrWhiteSpace(prompt))
        {
            terms.AddRange(TextTermExtractor.ExtractTerms(prompt.Replace('/', ' ')));
        }

        var candidates = EnumerateFiles(workspace.RepositoryPath)
            .Select(path => ScoreFile(workspace.RepositoryPath, path, terms, prompt))
            .Where(file => file.Score > 0)
            .OrderByDescending(file => file.Score)
            .ThenBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(maxFiles, 5, 75))
            .ToList();

        return new RepositoryAnalysis(workspace, candidates, terms.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    private IEnumerable<string> EnumerateFiles(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file);
            if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => IgnoredDirectories.Contains(part)))
            {
                continue;
            }

            if (IsRelevantExtension(Path.GetExtension(file)))
            {
                yield return file;
            }
        }
    }

    private static bool IsRelevantExtension(string extension) =>
        FileExtensionsOfInterest.Contains(extension, StringComparer.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(extension);

    private FileInsight ScoreFile(string root, string filePath, List<string> terms, string prompt)
    {
        var relativePath = Path.GetRelativePath(root, filePath);
        var extension = Path.GetExtension(filePath);
        var language = extension.Equals(".cs", StringComparison.OrdinalIgnoreCase) ? "CSharp" : extension.TrimStart('.').ToUpperInvariant();

        string text;
        try
        {
            var info = new FileInfo(filePath);
            if (info.Length > 1_000_000)
            {
                return EmptyInsight(filePath, relativePath, language);
            }

            text = File.ReadAllText(filePath, Encoding.UTF8);
        }
        catch
        {
            return EmptyInsight(filePath, relativePath, language);
        }

        if (LooksBinary(text))
        {
            return EmptyInsight(filePath, relativePath, language);
        }

        var matchedTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var score = 0.0;

        score += ScorePath(relativePath, terms, matchedTerms);
        score += ScoreContent(text, terms, matchedTerms, prompt);

        IReadOnlyList<string> symbols = [];
        IReadOnlyList<string> usings = [];

        if (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase))
        {
            var summary = _symbolExtractor.Extract(text);
            symbols = summary.Types.Concat(summary.Members).ToList();
            usings = summary.Usings;
            score += ScoreSymbols(symbols, terms, matchedTerms);
            score += summary.Types.Any(t => t.Contains("controller", StringComparison.OrdinalIgnoreCase)) ? 4 : 0;
            score += summary.Members.Any(m => m.Contains("validate", StringComparison.OrdinalIgnoreCase)) ? 3 : 0;
        }

        var evidence = BuildEvidence(relativePath, filePath, text, terms, prompt, score);
        var why = BuildWhy(relativePath, matchedTerms, symbols, usings, score);

        return new FileInsight
        {
            Path = filePath,
            RelativePath = relativePath,
            Language = language,
            Score = score,
            WhySelected = why,
            MatchedTerms = matchedTerms.ToList(),
            Symbols = symbols,
            Usings = usings,
            Evidence = evidence
        };
    }

    private static double ScorePath(string relativePath, IEnumerable<string> terms, HashSet<string> matchedTerms)
    {
        var score = 0.0;
        var pathSegments = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        foreach (var term in terms)
        {
            if (pathSegments.Any(segment => segment.Contains(term, StringComparison.OrdinalIgnoreCase)))
            {
                score += 4;
                matchedTerms.Add(term);
            }
        }

        if (relativePath.Contains("controller", StringComparison.OrdinalIgnoreCase))
        {
            score += 2;
        }

        if (relativePath.Contains("service", StringComparison.OrdinalIgnoreCase))
        {
            score += 2;
        }

        if (relativePath.Contains("validator", StringComparison.OrdinalIgnoreCase))
        {
            score += 3;
        }

        if (relativePath.Contains("test", StringComparison.OrdinalIgnoreCase))
        {
            score += 1.5;
        }

        return score;
    }

    private static double ScoreContent(string text, IEnumerable<string> terms, HashSet<string> matchedTerms, string prompt)
    {
        var score = 0.0;
        var lowerText = text.ToLowerInvariant();

        foreach (var term in terms)
        {
            if (lowerText.Contains(term.ToLowerInvariant(), StringComparison.Ordinal))
            {
                score += 3;
                matchedTerms.Add(term);
            }
        }

        foreach (var keyword in RuleKeywords)
        {
            if (lowerText.Contains(keyword, StringComparison.Ordinal))
            {
                score += 0.5;
            }
        }

        if (!string.IsNullOrWhiteSpace(prompt))
        {
            var promptTerms = TextTermExtractor.ExtractTerms(prompt);
            score += promptTerms.Count(term => lowerText.Contains(term, StringComparison.OrdinalIgnoreCase)) * 0.5;
        }

        if (Regex.IsMatch(text, @"\b(return\s+BadRequest|throw\s+new\s+\w*Exception|ModelState\.IsValid|RequiredAttribute|Authorize|Validate|FluentValidation)\b", RegexOptions.IgnoreCase))
        {
            score += 4;
        }

        return score;
    }

    private static double ScoreSymbols(IEnumerable<string> symbols, IEnumerable<string> terms, HashSet<string> matchedTerms)
    {
        var score = 0.0;
        foreach (var symbol in symbols)
        {
            foreach (var term in terms)
            {
                if (symbol.Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    score += 2.5;
                    matchedTerms.Add(term);
                }
            }
        }

        return score;
    }

    private static IReadOnlyList<EvidenceSnippet> BuildEvidence(string relativePath, string filePath, string text, IEnumerable<string> terms, string prompt, double fileScore)
    {
        var lines = text.Split('\n');
        var evidence = new List<EvidenceSnippet>();
        var seenLineNumbers = new HashSet<int>();
        var searchTerms = terms.Concat(TextTermExtractor.ExtractTerms(prompt)).Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToList();
        var lineNumber = 1;

        foreach (var line in lines)
        {
            var lowerLine = line.ToLowerInvariant();
            if (!searchTerms.Any(term => lowerLine.Contains(term.ToLowerInvariant(), StringComparison.Ordinal)))
            {
                lineNumber++;
                continue;
            }

            if (!seenLineNumbers.Add(lineNumber))
            {
                lineNumber++;
                continue;
            }

            var start = Math.Max(1, lineNumber - 1);
            var end = Math.Min(lines.Length, lineNumber + 1);
            var snippetLines = lines[(start - 1)..end];
            evidence.Add(new EvidenceSnippet
            {
                EvidenceId = $"EV-{Hashing.Sha256($"{relativePath}:{lineNumber}:{line.Trim()}")[..10]}",
                FilePath = filePath,
                LineStart = start,
                LineEnd = end,
                Snippet = string.Join(Environment.NewLine, snippetLines),
                Note = $"Matched search term in {relativePath}",
                Score = fileScore
            });

            if (evidence.Count >= 4)
            {
                break;
            }

            lineNumber++;
        }

        if (evidence.Count == 0)
        {
            var firstLines = lines.Take(Math.Min(3, lines.Length)).ToArray();
            evidence.Add(new EvidenceSnippet
            {
                EvidenceId = $"EV-{Hashing.Sha256(relativePath)[..10]}",
                FilePath = filePath,
                LineStart = 1,
                LineEnd = Math.Min(3, lines.Length),
                Snippet = string.Join(Environment.NewLine, firstLines),
                Note = $"General relevance from {relativePath}",
                Score = fileScore
            });
        }

        return evidence;
    }

    private static string BuildWhy(string relativePath, IEnumerable<string> matchedTerms, IEnumerable<string> symbols, IEnumerable<string> usings, double score)
    {
        var reasons = new List<string>();

        if (matchedTerms.Any())
        {
            reasons.Add($"matched terms: {string.Join(", ", matchedTerms.Distinct(StringComparer.OrdinalIgnoreCase).Take(4))}");
        }

        if (symbols.Any())
        {
            reasons.Add($"symbols: {string.Join(", ", symbols.Take(4))}");
        }

        if (usings.Any())
        {
            reasons.Add($"imports: {string.Join(", ", usings.Take(3))}");
        }

        if (relativePath.Contains("controller", StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add("controller entry point");
        }

        if (relativePath.Contains("validator", StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add("validation logic");
        }

        return $"{relativePath} | score={score:F1} | {string.Join("; ", reasons)}";
    }

    private static bool LooksBinary(string text) => text.Any(ch => ch == '\0');

    private static FileInsight EmptyInsight(string path, string relativePath, string language) => new()
    {
        Path = path,
        RelativePath = relativePath,
        Language = language,
        Score = 0,
        WhySelected = "Skipped because the file is not useful or too large to inspect safely.",
        MatchedTerms = [],
        Symbols = [],
        Usings = [],
        Evidence = []
    };
}

internal static class TextTermExtractor
{
    private static readonly Regex Splitter = new(@"[^A-Za-z0-9]+", RegexOptions.Compiled);

    public static List<string> ExtractTerms(string text)
    {
        var terms = new List<string>();
        foreach (var token in Splitter.Split(text ?? string.Empty))
        {
            var value = token.Trim().ToLowerInvariant();
            if (value.Length < 3)
            {
                continue;
            }

            terms.Add(value);
        }

        return terms;
    }
}

