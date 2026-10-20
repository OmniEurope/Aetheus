// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;

namespace Aetheus.Agent.Core.Operations;

/// <summary>Publish reliability.mutation.score as detected mutants over valid mutants, excluding
/// ignored and compile-error mutants. Fail without valid mutants and leave thresholds to versioned
/// quality gates.</summary>
internal static class PipelineMutationPublisher
{
    internal const string ScannerKey = "stryker-mutation";
    internal const string DefaultPattern = "**/mutation-report.json";

    internal sealed record MutationScore(long Killed, long Timeout, long Survived, long NoCoverage, long Excluded)
    {
        public long Valid => Killed + Timeout + Survived + NoCoverage;
        public double Percent => Valid == 0 ? 0 : 100.0 * (Killed + Timeout) / Valid;
    }

    public static async Task<ExecutorResult> PublishAsync(
        IServerApiClient apiClient, ILogger logger, TimeProvider timeProvider, string target,
        IReadOnlyDictionary<string, string> envVars,
        Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
    {
        var context = await PipelinePublicationContext.CreateAsync(envVars, timeProvider, onOutput).ConfigureAwait(false);
        if (context is null) return new ExecutorResult(-1, false);
        var (runId, stageName, baseDir, startedAt) = context;
        var patterns = PipelineTargetPatterns.ParseOptional(target) ?? [DefaultPattern];
        var files = patterns
            .SelectMany(pattern => WorkspaceFileMatcher.GetMatchingFiles(baseDir, pattern))
            .Where(File.Exists)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(file => file, StringComparer.Ordinal)
            .ToList();
        if (files.Count != 1)
        {
            await onOutput(
                files.Count == 0
                    ? $"No mutation report found matching {string.Join(", ", patterns)} - failing (mutation step published nothing)."
                    : $"Mutation publish requires one report; found {files.Count}.",
                TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(1, false);
        }

        var file = files[0];
        var relativePath = Path.GetRelativePath(baseDir, file);
        var score = TryReadScore(await File.ReadAllTextAsync(file, ct).ConfigureAwait(false));
        if (score is null || score.Valid == 0)
        {
            await onOutput(
                score is null
                    ? $"{relativePath} is not a mutation-testing-elements report."
                    : $"{relativePath} has no valid mutant: no score to publish.",
                TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(1, false);
        }

        await onOutput(
            $"Publishing mutation score from {relativePath}: {score.Percent:0.##}% "
            + $"(killed {score.Killed}, timeout {score.Timeout}, survived {score.Survived}, no coverage {score.NoCoverage}; "
            + $"{score.Excluded} not counted)", TaskLogLevel.Info).ConfigureAwait(false);
        try
        {
            var artifactId = await AnalysisArtifactUploader.UploadFileAsync(
                apiClient, runId, $"analysis-{ScannerKey}", stageName, relativePath, file, ct).ConfigureAwait(false);
            var analysis = await apiClient.PublishAnalysisReportAsync(runId,
                BuildRequest(score, relativePath, artifactId, stageName, startedAt, timeProvider.GetUtcNow().UtcDateTime),
                ct).ConfigureAwait(false);
            if (analysis is null)
            {
                await onOutput("Mutation analysis publication returned no result.", TaskLogLevel.Error).ConfigureAwait(false);
                return new ExecutorResult(1, false);
            }
            if (analysis.GateStatus is AnalysisGateStatus.Blocked or AnalysisGateStatus.Error)
                await onOutput($"Mutation verdict deferred to the common gate: {analysis.GateStatus}.", TaskLogLevel.Warning).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to publish the mutation score for run {RunId}", runId);
            await onOutput($"Mutation publish failed: {ex.Message}", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        await onOutput("Mutation score published successfully", TaskLogLevel.Info).ConfigureAwait(false);
        return new ExecutorResult(0, false);
    }

    /// <summary>Counts the mutants of every file; null when the document is not such a report.</summary>
    internal static MutationScore? TryReadScore(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Object)
                return null;
            long killed = 0, timeout = 0, survived = 0, noCoverage = 0, excluded = 0;
            foreach (var file in files.EnumerateObject())
            {
                if (!file.Value.TryGetProperty("mutants", out var mutants) || mutants.ValueKind != JsonValueKind.Array)
                    return null;
                foreach (var mutant in mutants.EnumerateArray())
                {
                    var status = mutant.TryGetProperty("status", out var value) && value.ValueKind == JsonValueKind.String
                        ? value.GetString()
                        : null;
                    switch (status)
                    {
                        case "Killed": killed++; break;
                        case "Timeout": timeout++; break;
                        case "Survived": survived++; break;
                        case "NoCoverage": noCoverage++; break;
                        default: excluded++; break;
                    }
                }
            }
            return new MutationScore(killed, timeout, survived, noCoverage, excluded);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static PublishAnalysisReportRequest BuildRequest(
        MutationScore score, string relativePath, int? artifactId,
        string? stageName, DateTime startedAt, DateTime completedAt)
    {
        var toolName = "Stryker.NET";
        // The version is the one the project's tool manifest pins; nothing on the task carries it.
        const string toolVersion = "project-lock";
        return new PublishAnalysisReportRequest
        {
            ScannerKey = ScannerKey,
            ScannerName = toolName,
            ScannerVersion = toolVersion,
            Category = AnalysisCategory.CodeQuality,
            Status = AnalysisReportStatus.Passed,
            Format = AnalysisReportFormat.MetricsJson,
            ReportContent = JsonSerializer.Serialize(new
            {
                metrics = new[]
                {
                    new { key = "reliability.mutation.score", value = score.Percent, unit = "percent", scope = "project", language = "csharp", toolName, direction = "HigherIsBetter" },
                    new { key = "reliability.mutation.detected", value = (double)(score.Killed + score.Timeout), unit = "mutants", scope = "project", language = "csharp", toolName, direction = "HigherIsBetter" },
                    new { key = "reliability.mutation.undetected", value = (double)(score.Survived + score.NoCoverage), unit = "mutants", scope = "project", language = "csharp", toolName, direction = "LowerIsBetter" },
                    new { key = "reliability.mutation.valid", value = (double)score.Valid, unit = "mutants", scope = "project", language = "csharp", toolName, direction = "Informational" }
                }
            }),
            ReportPath = relativePath,
            PipelineArtifactId = artifactId,
            StageName = stageName,
            StepName = relativePath,
            StartedAt = startedAt,
            CompletedAt = completedAt
        };
    }
}
