using System.Text.RegularExpressions;
using TestAIPoc.Infrastructure;
using TestAIPoc.Models;

namespace TestAIPoc.Composition;

public sealed class HeuristicTestCaseComposer
{
    public IReadOnlyList<TestCaseDocument> Compose(PipelineRun run, ContextDocument context, RepositoryAnalysis analysis)
    {
        var cases = new List<TestCaseDocument>();
        var sourceRules = context.BusinessRules.Concat(context.Validations).Concat(context.EdgeCases).ToList();

        AddHappyPathCase(cases, run, context, sourceRules);
        AddValidationCases(cases, run, context, sourceRules);
        AddNegativeCases(cases, run, context, sourceRules);
        AddBoundaryCases(cases, run, context, sourceRules);
        AddAuthorizationCases(cases, run, context, sourceRules);

        return cases
            .GroupBy(testCase => testCase.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static void AddHappyPathCase(List<TestCaseDocument> cases, PipelineRun run, ContextDocument context, IReadOnlyList<ContextStatement> rules)
    {
        var traceability = rules.Take(3).Select(rule => rule.Id).ToList();
        cases.Add(NewCase(
            run,
            context,
            title: $"Successful {context.ModuleName} request completes end-to-end",
            category: "functional",
            priority: "high",
            risk: "high",
            preconditions: [
                $"Repository commit under test: {context.Repository.CommitSha}",
                $"Target module: {context.ModuleName}"
            ],
            steps: [
                $"Open the `{context.ModuleName}` workflow or endpoint.",
                "Provide a valid, complete input payload.",
                "Submit the request and observe the persisted result."
            ],
            expectedResults: [
                "The request is accepted.",
                "All required validations pass.",
                "The expected business outcome is recorded without errors."
            ],
            traceability: traceability,
            notes: "Generated from the top-ranked context rules."));
    }

    private static void AddValidationCases(List<TestCaseDocument> cases, PipelineRun run, ContextDocument context, IReadOnlyList<ContextStatement> rules)
    {
        var validationInputs = context.Validations.Count > 0 ? context.Validations : rules;
        foreach (var validation in validationInputs.Take(4))
        {
            cases.Add(NewCase(
                run,
                context,
                title: $"Reject invalid input for {context.ModuleName}: {Shorten(validation.Text, 36)}",
                category: "validation",
                priority: "high",
                risk: "high",
                preconditions: [
                    $"Use the `{context.ModuleName}` entry point.",
                    "Prepare a payload that violates the target rule."
                ],
                steps: [
                    "Submit the payload with the specific field or state violation.",
                    "Capture the validation response."
                ],
                expectedResults: [
                    "The system rejects the request.",
                    "A clear validation message is returned.",
                    "No invalid transaction is committed."
                ],
                traceability: [validation.Id],
                notes: "Negative validation coverage derived from explicit rule text."));
        }
    }

    private static void AddNegativeCases(List<TestCaseDocument> cases, PipelineRun run, ContextDocument context, IReadOnlyList<ContextStatement> rules)
    {
        var duplicateHint = context.EdgeCases.FirstOrDefault(edge => edge.Text.Contains("duplicate", StringComparison.OrdinalIgnoreCase))
            ?? context.EdgeCases.FirstOrDefault();

        if (duplicateHint is not null)
        {
            cases.Add(NewCase(
                run,
                context,
                title: $"Duplicate {context.ModuleName} submission is handled safely",
                category: "negative",
                priority: "medium",
                risk: "medium",
                preconditions: [
                    "An initial valid request has already been processed."
                ],
                steps: [
                    "Repeat the same request with the same business key.",
                    "Observe whether the system blocks or deduplicates the request."
                ],
                expectedResults: [
                    "The duplicate request is either rejected or safely idempotent.",
                    "The primary business record remains consistent."
                ],
                traceability: new[] { duplicateHint.Id }.Concat(rules.Take(1).Select(rule => rule.Id)).ToList(),
                notes: "Derived from edge-case language around duplicates or repeated input."));
        }

        cases.Add(NewCase(
            run,
            context,
            title: $"Missing required {context.ModuleName} data is rejected",
            category: "negative",
            priority: "high",
            risk: "high",
            preconditions: [
                "The user has access to the target module.",
                "The request payload is missing a required field."
            ],
            steps: [
                "Submit a minimal payload that omits one required field.",
                "Record the system response."
            ],
            expectedResults: [
                "The system returns a validation failure.",
                "The missing field is identified in the response.",
                "No partial record is created."
            ],
            traceability: rules.Take(2).Select(rule => rule.Id).ToList(),
            notes: "Baseline negative case generated even when the repo slice does not spell out every field."));
    }

    private static void AddBoundaryCases(List<TestCaseDocument> cases, PipelineRun run, ContextDocument context, IReadOnlyList<ContextStatement> rules)
    {
        var boundaryRelevant = context.BusinessRules.Concat(context.Validations).FirstOrDefault(item =>
            Regex.IsMatch(item.Text, @"\b(max|minimum|min|max length|range|limit|threshold|days|hours|amount)\b", RegexOptions.IgnoreCase));

        var traceability = boundaryRelevant is null
            ? rules.Take(2).Select(rule => rule.Id).ToList()
            : new[] { boundaryRelevant.Id }.ToList();

        cases.Add(NewCase(
            run,
            context,
            title: $"Boundary input values for {context.ModuleName} are validated",
            category: "boundary",
            priority: "medium",
            risk: "medium",
            preconditions: [
                "A field with a numeric or date constraint is identified."
            ],
            steps: [
                "Submit the minimum allowed value.",
                "Submit the maximum allowed value.",
                "Submit a value just outside the allowed range."
            ],
            expectedResults: [
                "Values inside the allowed range are accepted.",
                "Values outside the range are rejected with a clear message."
            ],
            traceability: traceability,
            notes: "Generic boundary coverage to protect against off-by-one and limit regressions."));
    }

    private static void AddAuthorizationCases(List<TestCaseDocument> cases, PipelineRun run, ContextDocument context, IReadOnlyList<ContextStatement> rules)
    {
        object? authorizationHint = context.Validations.FirstOrDefault(item => item.Text.Contains("authorize", StringComparison.OrdinalIgnoreCase));
        authorizationHint ??= context.BusinessRules.FirstOrDefault(item => item.Text.Contains("role", StringComparison.OrdinalIgnoreCase));
        authorizationHint ??= context.EvidenceFiles.SelectMany(file => file.Evidence)
            .FirstOrDefault(evidence => evidence.Snippet.Contains("Authorize", StringComparison.OrdinalIgnoreCase) || evidence.Snippet.Contains("role", StringComparison.OrdinalIgnoreCase));

        if (authorizationHint is ContextStatement statement)
        {
            cases.Add(NewCase(
                run,
                context,
                title: $"Unauthorized access to {context.ModuleName} is blocked",
                category: "security",
                priority: "high",
                risk: "high",
                preconditions: [
                    "Use a user without the required role or permission."
                ],
                steps: [
                    "Attempt to access the protected flow.",
                    "Record the authorization response."
                ],
                expectedResults: [
                    "The request is denied.",
                    "The user receives an authorization failure rather than a generic error."
                ],
                traceability: [statement.Id],
                notes: "Authorization coverage anchored to explicit rule text."));
        }
        else if (authorizationHint is EvidenceSnippet evidence)
        {
            cases.Add(NewCase(
                run,
                context,
                title: $"Unauthorized access to {context.ModuleName} is blocked",
                category: "security",
                priority: "high",
                risk: "high",
                preconditions: [
                    "Use a user without the required role or permission."
                ],
                steps: [
                    "Attempt to access the protected flow.",
                    "Record the authorization response."
                ],
                expectedResults: [
                    "The request is denied.",
                    "The user receives an authorization failure rather than a generic error."
                ],
                traceability: [evidence.EvidenceId],
                notes: "Authorization coverage anchored to evidence text."));
        }
        else
        {
            cases.Add(NewCase(
                run,
                context,
                title: $"Unauthorized access guardrail for {context.ModuleName}",
                category: "security",
                priority: "medium",
                risk: "medium",
                preconditions: [
                    "An authenticated user lacks the target permission."
                ],
                steps: [
                    "Attempt to invoke the module with a restricted account.",
                    "Capture the failure response."
                ],
                expectedResults: [
                    "Access is denied if the module is protected.",
                    "The behavior is documented even if no explicit repo clue was found."
                ],
                traceability: rules.Take(1).Select(rule => rule.Id).ToList(),
                notes: "Fallback security case when explicit authorization evidence is absent."));
        }
    }

    private static TestCaseDocument NewCase(
        PipelineRun run,
        ContextDocument context,
        string title,
        string category,
        string priority,
        string risk,
        IReadOnlyList<string> preconditions,
        IReadOnlyList<string> steps,
        IReadOnlyList<string> expectedResults,
        IReadOnlyList<string> traceability,
        string notes)
    {
        var keySource = $"{run.RunId}|{context.Repository.CommitSha}|{title}|{category}";
        var id = $"TC-{Hashing.Sha256(keySource)[..12]}";

        return new TestCaseDocument
        {
            Id = id,
            Title = title,
            Category = category,
            Severity = CompositionHelpers.DeriveSeverity(category, risk),
            TestType = CompositionHelpers.DeriveTestType(category),
            AutomationSuitability = CompositionHelpers.DeriveAutomationSuitability(category, risk),
            Priority = priority,
            Risk = risk,
            Preconditions = preconditions,
            Steps = steps,
            ExpectedResults = expectedResults,
            Traceability = traceability,
            Notes = notes,
            OriginCommit = context.Repository.CommitSha,
            RunId = run.RunId
        };
    }

    private static string Shorten(string text, int length) =>
        text.Length <= length ? text : text[..Math.Max(1, length - 3)] + "...";
}
