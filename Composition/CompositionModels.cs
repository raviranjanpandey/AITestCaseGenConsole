using System.Text.Json.Serialization;

namespace TestAIPoc.Composition;

public sealed record GeneratedTestCaseBatch
{
    [JsonPropertyName("model")]
    public string Model { get; init; } = string.Empty;

    [JsonPropertyName("summary")]
    public string Summary { get; init; } = string.Empty;

    [JsonPropertyName("test_cases")]
    public List<GeneratedTestCase> TestCases { get; init; } = [];
}

public sealed record GeneratedTestCase
{
    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; init; } = string.Empty;

    [JsonPropertyName("severity")]
    public string Severity { get; init; } = string.Empty;

    [JsonPropertyName("test_type")]
    public string TestType { get; init; } = string.Empty;

    [JsonPropertyName("automation_suitability")]
    public string AutomationSuitability { get; init; } = string.Empty;

    [JsonPropertyName("priority")]
    public string Priority { get; init; } = string.Empty;

    [JsonPropertyName("risk")]
    public string Risk { get; init; } = string.Empty;

    [JsonPropertyName("preconditions")]
    public List<string> Preconditions { get; init; } = [];

    [JsonPropertyName("steps")]
    public List<string> Steps { get; init; } = [];

    [JsonPropertyName("expected_results")]
    public List<string> ExpectedResults { get; init; } = [];

    [JsonPropertyName("traceability")]
    public List<string> Traceability { get; init; } = [];

    [JsonPropertyName("notes")]
    public string Notes { get; init; } = string.Empty;
}

internal static class TestCaseGenerationSchema
{
    public static byte[] JsonSchemaUtf8 => """
    {
      "type": "object",
      "properties": {
        "model": { "type": "string" },
        "summary": { "type": "string" },
        "test_cases": {
          "type": "array",
          "minItems": 6,
          "items": {
            "type": "object",
            "properties": {
              "title":                  { "type": "string" },
              "category":               { "type": "string" },
              "severity":               { "type": "string", "enum": ["critical", "high", "medium", "low"] },
              "test_type":              { "type": "string", "enum": ["functional", "validation", "negative", "boundary", "security", "integration", "regression"] },
              "automation_suitability": { "type": "string", "enum": ["automatable", "partial", "manual"] },
              "priority":               { "type": "string", "enum": ["high", "medium", "low"] },
              "risk":                   { "type": "string", "enum": ["high", "medium", "low"] },
              "preconditions":    { "type": "array", "items": { "type": "string" } },
              "steps":            { "type": "array", "items": { "type": "string" } },
              "expected_results": { "type": "array", "items": { "type": "string" } },
              "traceability":     { "type": "array", "items": { "type": "string" } },
              "notes":            { "type": "string" }
            },
            "required": ["title", "category", "severity", "test_type", "automation_suitability", "priority", "risk", "preconditions", "steps", "expected_results", "traceability", "notes"],
            "additionalProperties": false
          }
        }
      },
      "required": ["model", "summary", "test_cases"],
      "additionalProperties": false
    }
    """u8.ToArray();
}

public static class CompositionHelpers
{
    public static string DeriveSeverity(string category, string risk) =>
        category switch
        {
            "security"   => "high",
            "validation" => "high",
            "negative"   => risk.Equals("high", StringComparison.OrdinalIgnoreCase) ? "high" : "medium",
            "boundary"   => "medium",
            _ => risk.Equals("high", StringComparison.OrdinalIgnoreCase) ? "high" : "medium"
        };

    public static string DeriveTestType(string category) =>
        category switch
        {
            "validation" => "validation",
            "negative"   => "negative",
            "boundary"   => "boundary",
            "security"   => "security",
            "functional" => "functional",
            _ => "regression"
        };

    public static string DeriveAutomationSuitability(string category, string risk) =>
        category switch
        {
            "security"   => "partial",
            "negative"   => "automatable",
            "boundary"   => "automatable",
            "validation" => "automatable",
            _ => risk.Equals("high", StringComparison.OrdinalIgnoreCase) ? "partial" : "automatable"
        };
}
