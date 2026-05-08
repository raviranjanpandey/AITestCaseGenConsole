using System.Text.RegularExpressions;

namespace TestAIPoc.Analysis;

public sealed record CSharpSymbolSummary(
    string Namespace,
    IReadOnlyList<string> Types,
    IReadOnlyList<string> Members,
    IReadOnlyList<string> Usings);

public sealed class CSharpSymbolExtractor
{
    private static readonly Regex NamespaceRegex = new(@"^\s*namespace\s+([A-Za-z_][A-Za-z0-9_.]*)", RegexOptions.Compiled);
    private static readonly Regex TypeRegex = new(@"^\s*(?:public|internal|private|protected|sealed|abstract|partial|static|\s)*\b(class|interface|record|struct|enum)\s+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);
    private static readonly Regex MethodRegex = new(@"^\s*(?:public|internal|private|protected|static|virtual|override|async|sealed|partial|extern|new|\s)+[A-Za-z0-9_<>\[\],\s?]+\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(", RegexOptions.Compiled);
    private static readonly Regex PropertyRegex = new(@"^\s*(?:public|internal|private|protected|static|virtual|override|async|sealed|partial|new|\s)+[A-Za-z0-9_<>\[\],\s?]+\s+([A-Za-z_][A-Za-z0-9_]*)\s*\{\s*(?:get|set)", RegexOptions.Compiled);

    private static readonly HashSet<string> MethodNoise = new(StringComparer.OrdinalIgnoreCase)
    {
        "if", "for", "foreach", "while", "switch", "catch", "try", "using", "lock", "return", "new", "do"
    };

    public CSharpSymbolSummary Extract(string source)
    {
        var namespaceName = string.Empty;
        var types = new List<string>();
        var members = new List<string>();
        var usings = new List<string>();

        foreach (var rawLine in source.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var namespaceMatch = NamespaceRegex.Match(line);
            if (namespaceMatch.Success)
            {
                namespaceName = namespaceMatch.Groups[1].Value;
            }

            if (TryCaptureUsing(line, out var usingName))
            {
                usings.Add(usingName);
            }

            var typeMatch = TypeRegex.Match(line);
            if (typeMatch.Success)
            {
                types.Add($"{typeMatch.Groups[1].Value} {typeMatch.Groups[2].Value}");
            }

            var methodMatch = MethodRegex.Match(line);
            if (methodMatch.Success)
            {
                var methodName = methodMatch.Groups[1].Value;
                if (!MethodNoise.Contains(methodName))
                {
                    members.Add($"method {methodName}");
                }
            }

            var propertyMatch = PropertyRegex.Match(line);
            if (propertyMatch.Success)
            {
                var propertyName = propertyMatch.Groups[1].Value;
                if (!MethodNoise.Contains(propertyName))
                {
                    members.Add($"property {propertyName}");
                }
            }
        }

        return new CSharpSymbolSummary(
            namespaceName,
            types.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            members.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            usings.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static bool TryCaptureUsing(string line, out string usingName)
    {
        usingName = string.Empty;

        if (line.StartsWith("using var ", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("using static ", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("global using var ", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("global using static ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var trimmed = line.StartsWith("global using ", StringComparison.OrdinalIgnoreCase)
            ? line["global using ".Length..].Trim()
            : line.StartsWith("using ", StringComparison.OrdinalIgnoreCase)
                ? line["using ".Length..].Trim()
                : string.Empty;

        if (trimmed.Length == 0)
        {
            return false;
        }

        if (!trimmed.EndsWith(';'))
        {
            return false;
        }

        trimmed = trimmed[..^1].Trim();
        if (trimmed.Length == 0 || trimmed.Equals("var", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("static", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        usingName = trimmed;
        return true;
    }
}
