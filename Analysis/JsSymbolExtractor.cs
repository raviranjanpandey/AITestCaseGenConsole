using System.Text.RegularExpressions;

namespace TestAIPoc.Analysis;

public sealed record JsSymbolSummary(
    IReadOnlyList<string> Exports,
    IReadOnlyList<string> Imports,
    IReadOnlyList<string> Hooks,
    IReadOnlyList<string> Components);

public sealed class JsSymbolExtractor
{
    // export function Foo / export async function Foo / export default function Foo
    private static readonly Regex ExportFn = new(
        @"^export\s+(?:default\s+)?(?:async\s+)?function\s*\*?\s+([A-Za-z_$][A-Za-z0-9_$]*)",
        RegexOptions.Compiled);

    // export const Foo / export default const Foo
    private static readonly Regex ExportConst = new(
        @"^export\s+(?:default\s+)?const\s+([A-Za-z_$][A-Za-z0-9_$]*)",
        RegexOptions.Compiled);

    // export class / export abstract class
    private static readonly Regex ExportClass = new(
        @"^export\s+(?:default\s+)?(?:abstract\s+)?class\s+([A-Za-z_$][A-Za-z0-9_$]*)",
        RegexOptions.Compiled);

    // export interface Foo / export type Foo / export enum Foo
    private static readonly Regex ExportType = new(
        @"^export\s+(?:interface|type|enum)\s+([A-Za-z_$][A-Za-z0-9_$]*)",
        RegexOptions.Compiled);

    // import ... from 'path'
    private static readonly Regex ImportFrom = new(
        @"^import\s+.+?\s+from\s+['""]([^'""]+)['""]",
        RegexOptions.Compiled);

    // const useFoo = / export const useFoo =
    private static readonly Regex HookDecl = new(
        @"(?:^|export\s+)const\s+(use[A-Z][A-Za-z0-9_$]*)\s*=",
        RegexOptions.Compiled);

    // PascalCase component: function Foo( or const Foo =
    private static readonly Regex ComponentDecl = new(
        @"^(?:export\s+)?(?:default\s+)?(?:async\s+)?(?:function\s+|const\s+)([A-Z][A-Za-z0-9_$]*)\s*[=(]",
        RegexOptions.Compiled);

    public JsSymbolSummary Extract(string source)
    {
        var exports    = new List<string>();
        var imports    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hooks      = new HashSet<string>(StringComparer.Ordinal);
        var components = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rawLine in source.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith("*", StringComparison.Ordinal))
                continue;

            TryCapture(ExportFn.Match(line),    "fn",        exports);
            TryCapture(ExportConst.Match(line), "const",     exports);
            TryCapture(ExportClass.Match(line), "class",     exports);
            TryCapture(ExportType.Match(line),  "type",      exports);

            var im = ImportFrom.Match(line);
            if (im.Success)
                imports.Add(im.Groups[1].Value);

            foreach (Match h in HookDecl.Matches(line))
                hooks.Add($"hook {h.Groups[1].Value}");

            var cm = ComponentDecl.Match(line);
            if (cm.Success && !hooks.Contains($"hook {cm.Groups[1].Value}"))
                components.Add($"component {cm.Groups[1].Value}");
        }

        return new JsSymbolSummary(
            exports.Distinct(StringComparer.Ordinal).ToList(),
            imports.ToList(),
            hooks.ToList(),
            components.ToList());
    }

    private static void TryCapture(Match m, string prefix, ICollection<string> target)
    {
        if (m.Success)
            target.Add($"{prefix} {m.Groups[1].Value}");
    }
}
