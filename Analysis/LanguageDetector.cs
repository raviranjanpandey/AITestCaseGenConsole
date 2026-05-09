namespace TestAIPoc.Analysis;

public static class LanguageDetector
{
    private static readonly Dictionary<string, string> ExtToLanguage = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"]    = "CSharp",
        [".ts"]    = "TypeScript",
        [".tsx"]   = "TypeScript",
        [".js"]    = "JavaScript",
        [".jsx"]   = "JavaScript",
        [".py"]    = "Python",
        [".java"]  = "Java",
        [".kt"]    = "Kotlin",
        [".go"]    = "Go",
        [".rb"]    = "Ruby",
        [".rs"]    = "Rust",
        [".swift"] = "Swift",
        [".php"]   = "PHP",
        [".cpp"]   = "C++",
        [".c"]     = "C",
        [".vue"]   = "Vue",
        [".sql"]   = "SQL",
    };

    public static (IReadOnlyList<string> Languages, string Primary) Detect(IEnumerable<string> filePaths)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in filePaths)
        {
            var ext = Path.GetExtension(path);
            if (ExtToLanguage.TryGetValue(ext, out var lang))
                counts[lang] = counts.GetValueOrDefault(lang) + 1;
        }

        var sorted = counts
            .OrderByDescending(kv => kv.Value)
            .Select(kv => kv.Key)
            .ToList();

        return (sorted, sorted.Count > 0 ? sorted[0] : "Unknown");
    }

    public static bool IsJsFamily(string ext) =>
        ext.Equals(".js", StringComparison.OrdinalIgnoreCase)  ||
        ext.Equals(".jsx", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".ts", StringComparison.OrdinalIgnoreCase)  ||
        ext.Equals(".tsx", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".vue", StringComparison.OrdinalIgnoreCase);

    private static readonly HashSet<string> BackendLanguages = new(StringComparer.OrdinalIgnoreCase)
        { "CSharp", "Java", "Kotlin", "Python", "Go", "Ruby", "PHP", "Rust", "Swift" };

    private static readonly HashSet<string> FrontendLanguages = new(StringComparer.OrdinalIgnoreCase)
        { "TypeScript", "JavaScript", "Vue" };

    public static bool IsFullStack(IReadOnlyList<string> languages) =>
        languages.Any(l => BackendLanguages.Contains(l)) &&
        languages.Any(l => FrontendLanguages.Contains(l));

    public static string GetStackType(IReadOnlyList<string> languages)
    {
        var hasBackend  = languages.Any(l => BackendLanguages.Contains(l));
        var hasFrontend = languages.Any(l => FrontendLanguages.Contains(l));
        return (hasBackend, hasFrontend) switch
        {
            (true,  true)  => "Full-Stack",
            (true,  false) => "Backend",
            (false, true)  => "Frontend",
            _              => "Unknown"
        };
    }
}
