using System.Text;
using System.Text.RegularExpressions;
using TestAIPoc.Infrastructure;
using TestAIPoc.Models;

namespace TestAIPoc.Analysis;

public sealed class RepositoryAnalyzer
{
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", "node_modules", ".vs", ".idea", "dist", "publish", ".next",
        "out", "build", "coverage", ".nyc_output", "__pycache__", ".pytest_cache", "venv", ".venv"
    };

    private static readonly string[] FileExtensionsOfInterest =
    [
        ".cs", ".json", ".yml", ".yaml", ".xml", ".md", ".txt",
        ".razor", ".ts", ".js", ".tsx", ".jsx", ".sql",
        ".py", ".java", ".go", ".rb", ".rs", ".kt", ".php", ".swift", ".vue"
    ];

    private static readonly string[] RuleKeywords =
    [
        "must", "should", "required", "cannot", "can't", "valid", "validation",
        "reject", "allow", "only", "unless", "if", "when", "authorize",
        "forbidden", "permission", "policy", "constraint"
    ];

    private readonly CSharpSymbolExtractor _csExtractor = new();
    private readonly JsSymbolExtractor _jsExtractor = new();

    public RepositoryAnalysis Analyze(RepositoryWorkspace workspace, string prompt, int maxFiles)
    {
        var terms = ExtractTerms(prompt);
        var allFiles = EnumerateFiles(workspace.RepositoryPath).ToList();
        var (detectedLanguages, primaryLanguage) = LanguageDetector.Detect(allFiles);

        var candidates = allFiles
            .Select(path => ScoreFile(workspace.RepositoryPath, path, terms, prompt, layer: null))
            .Where(file => file.Score > 0)
            .OrderByDescending(file => file.Score)
            .ThenBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(maxFiles, 5, 75))
            .ToList();

        return new RepositoryAnalysis(
            workspace,
            candidates,
            terms.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            detectedLanguages,
            primaryLanguage,
            ClientWorkspace: null);
    }

    public RepositoryAnalysis Analyze(RepositoryWorkspace serverWorkspace, RepositoryWorkspace clientWorkspace,
        string prompt, int maxFiles)
    {
        var terms = ExtractTerms(prompt);
        var perLayer = Math.Clamp(maxFiles / 2, 4, 37);

        var serverFiles = EnumerateFiles(serverWorkspace.RepositoryPath).ToList();
        var clientFiles = EnumerateFiles(clientWorkspace.RepositoryPath).ToList();

        var serverTop = serverFiles
            .Select(p => ScoreFile(serverWorkspace.RepositoryPath, p, terms, prompt, "server"))
            .Where(f => f.Score > 0)
            .OrderByDescending(f => f.Score)
            .ThenBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(perLayer)
            .ToList();

        var clientTop = clientFiles
            .Select(p => ScoreFile(clientWorkspace.RepositoryPath, p, terms, prompt, "client"))
            .Where(f => f.Score > 0)
            .OrderByDescending(f => f.Score)
            .ThenBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(perLayer)
            .ToList();

        var combined = serverTop.Concat(clientTop)
            .OrderByDescending(f => f.Score)
            .ThenBy(f => f.Layer, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var allFiles = serverFiles.Concat(clientFiles).ToList();
        var (detectedLanguages, primaryLanguage) = LanguageDetector.Detect(allFiles);

        return new RepositoryAnalysis(
            serverWorkspace,
            combined,
            terms.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            detectedLanguages,
            primaryLanguage,
            clientWorkspace);
    }

    private static List<string> ExtractTerms(string prompt)
    {
        var terms = TextTermExtractor.ExtractTerms(prompt);
        if (!string.IsNullOrWhiteSpace(prompt))
            terms.AddRange(TextTermExtractor.ExtractTerms(prompt.Replace('/', ' ')));
        return terms;
    }

    private IEnumerable<string> EnumerateFiles(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file);
            if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        .Any(part => IgnoredDirectories.Contains(part)))
                continue;

            if (IsRelevantExtension(Path.GetExtension(file)))
                yield return file;
        }
    }

    private static bool IsRelevantExtension(string extension) =>
        FileExtensionsOfInterest.Contains(extension, StringComparer.OrdinalIgnoreCase) ||
        string.IsNullOrWhiteSpace(extension);

    private FileInsight ScoreFile(string root, string filePath, List<string> terms, string prompt, string? layer)
    {
        var relativePath = Path.GetRelativePath(root, filePath);
        var extension = Path.GetExtension(filePath);
        var language = DetectFileLanguage(extension);

        string text;
        try
        {
            var info = new FileInfo(filePath);
            if (info.Length > 1_000_000)
                return EmptyInsight(filePath, relativePath, language, layer);

            text = File.ReadAllText(filePath, Encoding.UTF8);
        }
        catch
        {
            return EmptyInsight(filePath, relativePath, language, layer);
        }

        if (LooksBinary(text))
            return EmptyInsight(filePath, relativePath, language, layer);

        var matchedTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var score = 0.0;

        score += ScorePath(relativePath, terms, matchedTerms);
        score += ScoreContent(text, extension, terms, matchedTerms, prompt);

        IReadOnlyList<string> symbols = [];
        IReadOnlyList<string> usings = [];

        if (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase))
        {
            var summary = _csExtractor.Extract(text);
            symbols = summary.Types.Concat(summary.Members).ToList();
            usings = summary.Usings;
            score += ScoreSymbols(symbols, terms, matchedTerms);
            score += summary.Types.Any(t => t.Contains("controller", StringComparison.OrdinalIgnoreCase)) ? 4 : 0;
            score += summary.Members.Any(m => m.Contains("validate", StringComparison.OrdinalIgnoreCase)) ? 3 : 0;
        }
        else if (LanguageDetector.IsJsFamily(extension))
        {
            var summary = _jsExtractor.Extract(text);
            symbols = summary.Exports.Concat(summary.Hooks).Concat(summary.Components).ToList();
            usings = summary.Imports;
            score += ScoreSymbols(symbols, terms, matchedTerms);
            score += summary.Hooks.Count > 0 ? 3 : 0;
            score += summary.Components.Count > 0 ? 2 : 0;
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
            Evidence = evidence,
            Layer = layer
        };
    }

    private static double ScorePath(string relativePath, IEnumerable<string> terms, HashSet<string> matchedTerms)
    {
        var score = 0.0;
        var pathLower = relativePath.ToLowerInvariant();
        var segments = relativePath.Split(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries);

        foreach (var term in terms)
        {
            if (segments.Any(s => s.Contains(term, StringComparison.OrdinalIgnoreCase)))
            {
                score += 4;
                matchedTerms.Add(term);
            }
        }

        // C# / Java / general backend
        if (pathLower.Contains("controller"))  score += 2;
        if (pathLower.Contains("service"))     score += 2;
        if (pathLower.Contains("validator"))   score += 3;
        if (pathLower.Contains("repository"))  score += 2;
        if (pathLower.Contains("entity"))      score += 1.5;
        if (pathLower.Contains("middleware"))  score += 1.5;

        // React / JS / TS
        if (pathLower.Contains("component"))   score += 2;
        if (pathLower.Contains("hook"))        score += 2;
        if (pathLower.Contains("/page"))       score += 2;
        if (pathLower.Contains("/route"))      score += 2;
        if (pathLower.Contains("store"))       score += 1.5;
        if (pathLower.Contains("slice"))       score += 1.5;
        if (pathLower.Contains("reducer"))     score += 1.5;
        if (pathLower.Contains("/api/"))       score += 2;
        if (pathLower.Contains("context"))     score += 1.5;

        // Python / Django / Flask
        if (pathLower.Contains("view"))        score += 2;
        if (pathLower.Contains("serializer"))  score += 3;
        if (pathLower.Contains("urls"))        score += 1.5;
        if (pathLower.Contains("schema"))      score += 2;

        // Tests
        if (pathLower.Contains("test"))        score += 1.5;
        if (pathLower.Contains("spec"))        score += 1.5;

        return score;
    }

    private static double ScoreContent(string text, string extension, IEnumerable<string> terms,
        HashSet<string> matchedTerms, string prompt)
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
                score += 0.5;
        }

        if (!string.IsNullOrWhiteSpace(prompt))
        {
            var promptTerms = TextTermExtractor.ExtractTerms(prompt);
            score += promptTerms.Count(term => lowerText.Contains(term, StringComparison.OrdinalIgnoreCase)) * 0.5;
        }

        if (Regex.IsMatch(text, GetLanguagePattern(extension), RegexOptions.IgnoreCase))
            score += 4;

        if (Regex.IsMatch(text, @"\b(throw|error|validate|invalid|required|unauthorized|forbidden|reject|permission)\b", RegexOptions.IgnoreCase))
            score += 1;

        return score;
    }

    private static string GetLanguagePattern(string ext) => ext.ToLowerInvariant() switch
    {
        ".cs" =>
            @"\b(return\s+BadRequest|throw\s+new\s+\w*Exception|ModelState\.IsValid|RequiredAttribute|Authorize|Validate|FluentValidation)\b",
        ".ts" or ".tsx" or ".js" or ".jsx" or ".vue" =>
            @"\b(throw\s+new\s+Error|zod|yup|joi|z\.object|schema\.validate|HttpException|useReducer|dispatch\(|createSlice|createAsyncThunk|axios\.|fetch\()\b",
        ".py" =>
            @"\b(raise\s+\w*Exception|serializers\.|ValidationError|permission_classes|IsAuthenticated|@login_required|validate_|raise\s+Http404|raise\s+PermissionDenied)\b",
        ".java" or ".kt" =>
            @"\b(throw\s+new\s+\w*Exception|@Valid|@NotNull|@RequestMapping|ResponseEntity|HttpStatus|BindingResult|@PreAuthorize|@Authorize)\b",
        ".go" =>
            @"\b(errors\.New|fmt\.Errorf|http\.Error|http\.StatusBadRequest|http\.StatusUnauthorized|middleware|HandleFunc)\b",
        ".rb" =>
            @"\b(raise\s+|validates\s+|before_action|rescue_from|render\s+json:|render\s+:errors|authenticate_user!)\b",
        ".php" =>
            @"\b(throw\s+new\s+\w*Exception|Validator::|validate\(|abort\(|middleware|unauthorized|Gate::)\b",
        ".rs" =>
            @"\b(Err\(|unwrap_or_else|Result<|impl\s+std::error|#\[middleware\]|actix_web|warp::)\b",
        _ =>
            @"\b(error|invalid|required|validate|reject|forbidden|unauthorized|permission|constraint)\b"
    };

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

    private static IReadOnlyList<EvidenceSnippet> BuildEvidence(string relativePath, string filePath, string text,
        IEnumerable<string> terms, string prompt, double fileScore)
    {
        var lines = text.Split('\n');
        var evidence = new List<EvidenceSnippet>();
        var seenLineNumbers = new HashSet<int>();
        var searchTerms = terms.Concat(TextTermExtractor.ExtractTerms(prompt))
                               .Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToList();
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
            evidence.Add(new EvidenceSnippet
            {
                EvidenceId = $"EV-{Hashing.Sha256($"{relativePath}:{lineNumber}:{line.Trim()}")[..10]}",
                FilePath = filePath,
                LineStart = start,
                LineEnd = end,
                Snippet = string.Join(Environment.NewLine, lines[(start - 1)..end]),
                Note = $"Matched search term in {relativePath}",
                Score = fileScore
            });

            if (evidence.Count >= 4)
                break;

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

    private static string BuildWhy(string relativePath, IEnumerable<string> matchedTerms,
        IEnumerable<string> symbols, IEnumerable<string> usings, double score)
    {
        var reasons = new List<string>();

        if (matchedTerms.Any())
            reasons.Add($"matched terms: {string.Join(", ", matchedTerms.Distinct(StringComparer.OrdinalIgnoreCase).Take(4))}");

        if (symbols.Any())
            reasons.Add($"symbols: {string.Join(", ", symbols.Take(4))}");

        if (usings.Any())
            reasons.Add($"imports: {string.Join(", ", usings.Take(3))}");

        return $"{relativePath} | score={score:F1} | {string.Join("; ", reasons)}";
    }

    private static string DetectFileLanguage(string extension) => extension.ToLowerInvariant() switch
    {
        ".cs"    => "CSharp",
        ".ts"    => "TypeScript",
        ".tsx"   => "TypeScript",
        ".js"    => "JavaScript",
        ".jsx"   => "JavaScript",
        ".py"    => "Python",
        ".java"  => "Java",
        ".go"    => "Go",
        ".rb"    => "Ruby",
        ".rs"    => "Rust",
        ".kt"    => "Kotlin",
        ".swift" => "Swift",
        ".php"   => "PHP",
        ".vue"   => "Vue",
        _        => extension.TrimStart('.').ToUpperInvariant()
    };

    private static bool LooksBinary(string text) => text.Any(ch => ch == '\0');

    private static FileInsight EmptyInsight(string path, string relativePath, string language, string? layer) => new()
    {
        Path = path,
        RelativePath = relativePath,
        Language = language,
        Score = 0,
        WhySelected = "Skipped because the file is not useful or too large to inspect safely.",
        MatchedTerms = [],
        Symbols = [],
        Usings = [],
        Evidence = [],
        Layer = layer
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
                continue;

            terms.Add(value);
        }

        return terms;
    }
}
