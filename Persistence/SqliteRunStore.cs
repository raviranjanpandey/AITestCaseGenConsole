using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TestAIPoc.Models;

namespace TestAIPoc.Persistence;

public sealed class SqliteRunStore
{
    public string DatabasePath { get; }

    public SqliteRunStore(string databasePath)
    {
        DatabasePath = Path.GetFullPath(databasePath);
    }

    public async Task SaveAsync(PipelineResult result, bool overwriteDatabase)
    {
        var dbDirectory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrWhiteSpace(dbDirectory))
        {
            Directory.CreateDirectory(dbDirectory);
        }

        if (overwriteDatabase && File.Exists(DatabasePath))
        {
            File.Delete(DatabasePath);
        }

        var script = BuildSqlScript(result);
        await RunSqliteAsync(script);
    }

    private async Task RunSqliteAsync(string script)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = script;
        await command.ExecuteNonQueryAsync();
    }

    private static string BuildSqlScript(PipelineResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("BEGIN TRANSACTION;");
        builder.AppendLine("""
            CREATE TABLE IF NOT EXISTS Prompts (
                PromptId TEXT PRIMARY KEY,
                RunId TEXT NOT NULL,
                PromptText TEXT NOT NULL,
                ModuleName TEXT NOT NULL,
                CreatedUtc TEXT NOT NULL
            );
            """);
        builder.AppendLine("""
            CREATE TABLE IF NOT EXISTS Repositories (
                RepositoryId TEXT PRIMARY KEY,
                RepoKey TEXT NOT NULL,
                SourceDescription TEXT NOT NULL,
                RepositoryPath TEXT NOT NULL,
                CachePath TEXT NOT NULL,
                CommitSha TEXT NOT NULL,
                Branch TEXT NULL,
                IsGitRepository INTEGER NOT NULL,
                LastSeenUtc TEXT NOT NULL
            );
            """);
        builder.AppendLine("""
            CREATE TABLE IF NOT EXISTS Runs (
                RunId TEXT PRIMARY KEY,
                PromptId TEXT NOT NULL,
                RepositoryId TEXT NOT NULL,
                CreatedUtc TEXT NOT NULL,
                CompletedUtc TEXT NOT NULL,
                Status TEXT NOT NULL,
                GenerationProvider TEXT NOT NULL,
                ModelName TEXT NOT NULL,
                RawModelJson TEXT NOT NULL,
                ContextJsonPath TEXT NOT NULL,
                ContextMarkdownPath TEXT NOT NULL,
                ContextSummary TEXT NOT NULL
            );
            """);
        builder.AppendLine("""
            CREATE TABLE IF NOT EXISTS ContextArtifacts (
                ContextId TEXT PRIMARY KEY,
                RunId TEXT NOT NULL,
                RepositoryId TEXT NOT NULL,
                GeneratedUtc TEXT NOT NULL,
                ContextJson TEXT NOT NULL,
                ContextMarkdown TEXT NOT NULL
            );
            """);
        builder.AppendLine("""
            CREATE TABLE IF NOT EXISTS ContextEvidence (
                EvidenceRowId INTEGER PRIMARY KEY AUTOINCREMENT,
                ContextId TEXT NOT NULL,
                EvidenceId TEXT NOT NULL,
                FilePath TEXT NOT NULL,
                LineStart INTEGER NOT NULL,
                LineEnd INTEGER NOT NULL,
                Snippet TEXT NOT NULL,
                Note TEXT NOT NULL,
                Score REAL NOT NULL
            );
            """);
        builder.AppendLine("""
            CREATE TABLE IF NOT EXISTS ContextStatements (
                StatementRowId INTEGER PRIMARY KEY AUTOINCREMENT,
                ContextId TEXT NOT NULL,
                StatementId TEXT NOT NULL,
                Category TEXT NOT NULL,
                StatementText TEXT NOT NULL,
                EvidenceIds TEXT NOT NULL,
                Confidence TEXT NOT NULL
            );
            """);
        builder.AppendLine("""
            CREATE TABLE IF NOT EXISTS TestCases (
                TestCaseRowId INTEGER PRIMARY KEY AUTOINCREMENT,
                ContextId TEXT NOT NULL,
                RunId TEXT NOT NULL,
                TestCaseId TEXT NOT NULL,
                Title TEXT NOT NULL,
                Category TEXT NOT NULL,
                Severity TEXT NOT NULL,
                TestType TEXT NOT NULL,
                AutomationSuitability TEXT NOT NULL,
                Priority TEXT NOT NULL,
                Risk TEXT NOT NULL,
                PreconditionsJson TEXT NOT NULL,
                StepsJson TEXT NOT NULL,
                ExpectedResultsJson TEXT NOT NULL,
                TraceabilityJson TEXT NOT NULL,
                Notes TEXT NOT NULL,
                OriginCommit TEXT NOT NULL,
                RawJson TEXT NOT NULL
            );
            """);

        InsertPrompt(builder, result);
        InsertRepository(builder, result);
        InsertRun(builder, result);
        InsertContext(builder, result);
        InsertEvidence(builder, result);
        InsertStatements(builder, result);
        InsertTestCases(builder, result);

        builder.AppendLine("COMMIT;");
        return builder.ToString();
    }

    private static void InsertPrompt(StringBuilder builder, PipelineResult result)
    {
        builder.AppendLine($"""
            INSERT OR REPLACE INTO Prompts (PromptId, RunId, PromptText, ModuleName, CreatedUtc)
            VALUES ({Sql(result.Run.RunId, "prompt_")}, {Sql(result.Run.RunId)}, {Sql(result.Run.Prompt)}, {Sql(result.Run.ModuleName)}, {Sql(result.Run.CreatedUtc.UtcDateTime.ToString("O"))});
            """);
    }

    private static void InsertRepository(StringBuilder builder, PipelineResult result)
    {
        builder.AppendLine($"""
            INSERT OR REPLACE INTO Repositories (
                RepositoryId, RepoKey, SourceDescription, RepositoryPath, CachePath, CommitSha, Branch, IsGitRepository, LastSeenUtc
            )
            VALUES (
                {Sql(result.Workspace.RepoKey, "repo_")},
                {Sql(result.Workspace.RepoKey)},
                {Sql(result.Workspace.SourceDescription)},
                {Sql(result.Workspace.RepositoryPath)},
                {Sql(result.Workspace.CachePath)},
                {Sql(result.Workspace.CommitSha)},
                {Sql(result.Workspace.Branch)},
                {Sql(result.Workspace.IsGitRepository ? "1" : "0")},
                {Sql(DateTimeOffset.UtcNow.UtcDateTime.ToString("O"))}
            );
            """);
    }

    private static void InsertRun(StringBuilder builder, PipelineResult result)
    {
        builder.AppendLine($"""
            INSERT OR REPLACE INTO Runs (
                RunId, PromptId, RepositoryId, CreatedUtc, CompletedUtc, Status, GenerationProvider, ModelName, RawModelJson, ContextJsonPath, ContextMarkdownPath, ContextSummary
            )
            VALUES (
                {Sql(result.Run.RunId)},
                {Sql(result.Run.RunId, "prompt_")},
                {Sql(result.Workspace.RepoKey, "repo_")},
                {Sql(result.Run.CreatedUtc.UtcDateTime.ToString("O"))},
                {Sql(DateTimeOffset.UtcNow.UtcDateTime.ToString("O"))},
                {Sql("completed")},
                {Sql(result.GenerationProvider)},
                {Sql(result.ModelName)},
                {Sql(result.RawModelJson)},
                {Sql(result.ContextJsonPath)},
                {Sql(result.ContextMarkdownPath)},
                {Sql(result.Context.Summary)}
            );
            """);
    }

    private static void InsertContext(StringBuilder builder, PipelineResult result)
    {
        builder.AppendLine($"""
            INSERT OR REPLACE INTO ContextArtifacts (
                ContextId, RunId, RepositoryId, GeneratedUtc, ContextJson, ContextMarkdown
            )
            VALUES (
                {Sql(result.Run.RunId, "ctx_")},
                {Sql(result.Run.RunId)},
                {Sql(result.Workspace.RepoKey, "repo_")},
                {Sql(result.Context.GeneratedUtc.UtcDateTime.ToString("O"))},
                {Sql(JsonSerializer.Serialize(result.Context, JsonOptions.Pretty))},
                {Sql(File.ReadAllText(result.ContextMarkdownPath))}
            );
            """);
    }

    private static void InsertEvidence(StringBuilder builder, PipelineResult result)
    {
        foreach (var file in result.Analysis.SelectedFiles)
        {
            foreach (var evidence in file.Evidence)
            {
                builder.AppendLine($"""
                    INSERT INTO ContextEvidence (
                        ContextId, EvidenceId, FilePath, LineStart, LineEnd, Snippet, Note, Score
                    )
                    VALUES (
                        {Sql(result.Run.RunId, "ctx_")},
                        {Sql(evidence.EvidenceId)},
                        {Sql(evidence.FilePath)},
                        {Sql(evidence.LineStart.ToString())},
                        {Sql(evidence.LineEnd.ToString())},
                        {Sql(evidence.Snippet)},
                        {Sql(evidence.Note)},
                        {Sql(evidence.Score.ToString(System.Globalization.CultureInfo.InvariantCulture))}
                    );
                    """);
            }
        }
    }

    private static void InsertStatements(StringBuilder builder, PipelineResult result)
    {
        foreach (var statement in result.Context.BusinessRules
                     .Concat(result.Context.Validations)
                     .Concat(result.Context.Flows)
                     .Concat(result.Context.Dependencies)
                     .Concat(result.Context.EdgeCases)
                     .Concat(result.Context.Assumptions)
                     .Concat(result.Context.OpenQuestions))
        {
            builder.AppendLine($"""
                INSERT INTO ContextStatements (
                    ContextId, StatementId, Category, StatementText, EvidenceIds, Confidence
                )
                VALUES (
                    {Sql(result.Run.RunId, "ctx_")},
                    {Sql(statement.Id)},
                    {Sql(statement.Category)},
                    {Sql(statement.Text)},
                    {Sql(JsonSerializer.Serialize(statement.EvidenceIds))},
                    {Sql(statement.Confidence)}
                );
                """);
        }
    }

    private static void InsertTestCases(StringBuilder builder, PipelineResult result)
    {
        foreach (var testCase in result.TestCases)
        {
            builder.AppendLine($"""
                INSERT INTO TestCases (
                    ContextId, RunId, TestCaseId, Title, Category, Severity, TestType, AutomationSuitability, Priority, Risk,
                    PreconditionsJson, StepsJson, ExpectedResultsJson, TraceabilityJson, Notes, OriginCommit, RawJson
                )
                VALUES (
                    {Sql(result.Run.RunId, "ctx_")},
                    {Sql(result.Run.RunId)},
                    {Sql(testCase.Id)},
                    {Sql(testCase.Title)},
                    {Sql(testCase.Category)},
                    {Sql(testCase.Severity)},
                    {Sql(testCase.TestType)},
                    {Sql(testCase.AutomationSuitability)},
                    {Sql(testCase.Priority)},
                    {Sql(testCase.Risk)},
                    {Sql(JsonSerializer.Serialize(testCase.Preconditions))},
                    {Sql(JsonSerializer.Serialize(testCase.Steps))},
                    {Sql(JsonSerializer.Serialize(testCase.ExpectedResults))},
                    {Sql(JsonSerializer.Serialize(testCase.Traceability))},
                    {Sql(testCase.Notes)},
                    {Sql(testCase.OriginCommit)},
                    {Sql(JsonSerializer.Serialize(testCase, JsonOptions.Pretty))}
                );
                """);
        }
    }

    private static string Sql(string? value, string? prefix = null)
    {
        if (value is null)
        {
            return "NULL";
        }

        var text = prefix is null ? value : prefix + value;
        return "'" + text.Replace("'", "''") + "'";
    }
}
