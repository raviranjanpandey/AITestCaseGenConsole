namespace TestAIPoc.Composition;

public static class TestCaseComposerFactory
{
    public static ITestCaseComposer Create(string? provider, string? modelName, string? apiKey) =>
        (provider ?? "openai").Trim().ToLowerInvariant() switch
        {
            "claude" or "anthropic" => new AnthropicTestCaseComposer(
                modelName ?? "claude-sonnet-4-6", apiKey),
            _ => new OpenAiTestCaseComposer(
                modelName ?? "gpt-5.4-mini", apiKey)
        };
}
