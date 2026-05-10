using TestAIPoc.Models;

namespace TestAIPoc.Composition;

public interface ITestCaseComposer
{
    Task<TestCaseGenerationResult> ComposeAsync(
        PipelineRun run,
        ContextDocument context,
        RepositoryAnalysis analysis);
}
