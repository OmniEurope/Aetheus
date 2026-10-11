// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;

namespace Aetheus.Agent.Core.Operations;

internal static class PipelineQualityPublisher
{
    private static readonly string[] s_skipDirectories = ["bin", "obj", ".git", "node_modules", ".vs"];
    private const int MaxSourceFiles = 20_000;
    private const long MaxSourceBytes = 256L * 1024 * 1024;

    public static async Task<ExecutorResult> PublishComplexityAsync(
        IServerApiClient apiClient, ILogger logger, TimeProvider timeProvider, IReadOnlyDictionary<string, string> envVars,
        Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
    {
        var context = await PipelinePublicationContext.CreateAsync(envVars, timeProvider, onOutput).ConfigureAwait(false);
        if (context is null) return new ExecutorResult(-1, false);
        var (runId, stageName, baseDirectory, startedAt) = context;
        await onOutput($"Analyzing C# complexity in: {baseDirectory}", TaskLogLevel.Info).ConfigureAwait(false);
        var sourceLoad = await LoadSourcesAsync(baseDirectory, onOutput, ct).ConfigureAwait(false);
        if (sourceLoad.Failure is not null) return sourceLoad.Failure;
        var sources = sourceLoad.Sources;
        if (sources.Count == 0)
        {
            await onOutput("No C# source files found - failing (complexity step analyzed nothing).", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(1, false);
        }

        var parsedSources = CSharpSourceParser.Parse(sources, ct: ct);
        var report = ComplexityAnalyzer.Analyze(parsedSources);
        await onOutput(
            $"Analyzed {sources.Count} files · {report.TotalMethods} methods · avg CC {report.AvgCyclomatic} · max CC {report.MaxCyclomatic} · {report.HighComplexityMethods} over {ComplexityAnalyzer.HighComplexityThreshold} · {report.TotalLinesOfCode} LOC",
            TaskLogLevel.Info).ConfigureAwait(false);
        foreach (var hotspot in report.Hotspots.Take(10))
        {
            await onOutput(
                $"Complexity hotspot: CC {hotspot.Cyclomatic} · {hotspot.Path}:{hotspot.Line} · {hotspot.Member}",
                hotspot.Cyclomatic > 25 ? TaskLogLevel.Warning : TaskLogLevel.Info).ConfigureAwait(false);
        }
        return await PublishComplexityReportAsync(
            apiClient, logger, timeProvider, envVars, runId, stageName, baseDirectory,
            parsedSources, report, startedAt, onOutput, ct).ConfigureAwait(false);
    }

    private static async Task<SourceLoadResult> LoadSourcesAsync(
        string baseDirectory,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken ct)
    {
        var sources = new List<(string Path, string Content)>();
        long sourceBytes = 0;
        foreach (var file in EnumerateCsFiles(baseDirectory))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (sources.Count >= MaxSourceFiles)
                {
                    await onOutput(
                        $"C# analysis refused more than {MaxSourceFiles} source files.",
                        TaskLogLevel.Error).ConfigureAwait(false);
                    return SourceLoadResult.Failed(new ExecutorResult(1, false));
                }
                var fileBytes = new FileInfo(file).Length;
                if (sourceBytes + fileBytes > MaxSourceBytes)
                {
                    await onOutput(
                        $"C# analysis refused more than {MaxSourceBytes / (1024 * 1024)} MiB of source.",
                        TaskLogLevel.Error).ConfigureAwait(false);
                    return SourceLoadResult.Failed(new ExecutorResult(1, false));
                }
                sources.Add((file, await File.ReadAllTextAsync(file, ct).ConfigureAwait(false)));
                sourceBytes += fileBytes;
            }
            catch (IOException)
            {
                // An unreadable source cannot contribute to the report.
            }
        }
        return SourceLoadResult.Loaded(sources);
    }

    private static async Task<ExecutorResult> PublishComplexityReportAsync(
        IServerApiClient apiClient,
        ILogger logger,
        TimeProvider timeProvider,
        IReadOnlyDictionary<string, string> envVars,
        int runId,
        string? stageName,
        string baseDirectory,
        IReadOnlyList<ParsedCSharpSource> parsedSources,
        ComplexityReport report,
        DateTime startedAt,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken ct)
    {
        try
        {
            await apiClient.PublishComplexityAsync(runId, report.AvgCyclomatic, report.MaxCyclomatic,
                report.TotalMethods, report.HighComplexityMethods, report.TotalLinesOfCode, stageName, ct).ConfigureAwait(false);
            var metricsJson = JsonSerializer.Serialize(new
            {
                metrics = new object[]
                {
                    new { key = "complexity.cyclomatic.average", value = report.AvgCyclomatic, unit = "score", scope = "project", language = "csharp", toolName = "Microsoft.CodeAnalysis", direction = "LowerIsBetter" },
                    new { key = "complexity.cyclomatic.maximum", value = (double)report.MaxCyclomatic, unit = "score", scope = "project", language = "csharp", toolName = "Microsoft.CodeAnalysis", direction = "LowerIsBetter" },
                    new { key = "complexity.methods.total", value = (double)report.TotalMethods, unit = "methods", scope = "project", language = "csharp", toolName = "Microsoft.CodeAnalysis", direction = "Informational" },
                    new { key = "complexity.methods.high", value = (double)report.HighComplexityMethods, unit = "methods", scope = "project", language = "csharp", toolName = "Microsoft.CodeAnalysis", direction = "LowerIsBetter" },
                    new { key = "loc.total", value = (double)report.TotalLinesOfCode, unit = "lines", scope = "project", language = "csharp", toolName = "Microsoft.CodeAnalysis", direction = "Informational" }
                },
                hotspots = report.Hotspots
            });
            var artifactId = await AnalysisArtifactUploader.UploadTextAsync(
                apiClient, runId, "analysis-roslyn-metrics", stageName,
                "roslyn-metrics.json", metricsJson, ct).ConfigureAwait(false);
            var analysis = await apiClient.PublishAnalysisReportAsync(runId, new PublishAnalysisReportRequest
            {
                ScannerKey = "roslyn-metrics",
                ScannerName = "Microsoft.CodeAnalysis Metrics",
                ScannerVersion = typeof(Microsoft.CodeAnalysis.SyntaxTree).Assembly.GetName().Version?.ToString() ?? "unknown",
                Category = AnalysisCategory.CodeQuality,
                Status = AnalysisReportStatus.Passed,
                Format = AnalysisReportFormat.MetricsJson,
                ReportContent = metricsJson,
                ReportPath = "roslyn-metrics.json",
                PipelineArtifactId = artifactId,
                StageName = stageName,
                StartedAt = startedAt,
                CompletedAt = timeProvider.GetUtcNow().UtcDateTime
            }, ct).ConfigureAwait(false);
            if (analysis is null)
            {
                await onOutput("Complexity analysis publication returned no result.", TaskLogLevel.Error).ConfigureAwait(false);
                return new ExecutorResult(1, false);
            }
            if (analysis.GateStatus is AnalysisGateStatus.Blocked or AnalysisGateStatus.Error)
                await onOutput($"Complexity verdict deferred to the common gate: {analysis.GateStatus}.", TaskLogLevel.Warning).ConfigureAwait(false);
            await onOutput("Complexity metrics published successfully", TaskLogLevel.Info).ConfigureAwait(false);
            if (envVars.TryGetValue("AETHEUS_MAX_COMPLEXITY", out var maximumText)
                && int.TryParse(maximumText, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var maximum)
                && report.MaxCyclomatic > maximum)
            {
                var worst = report.Hotspots[0];
                await onOutput(
                    $"Complexity budget exceeded: max CC {report.MaxCyclomatic} > {maximum} at {worst.Path}:{worst.Line} ({worst.Member})",
                    TaskLogLevel.Error).ConfigureAwait(false);
                return new ExecutorResult(1, false);
            }
            return await ArchitectureAnalysisPublisher.PublishAsync(
                apiClient, timeProvider, runId, stageName, baseDirectory, parsedSources, onOutput, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to publish complexity for run {RunId}", runId);
            await onOutput($"Complexity publish failed: {ex.Message}", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }
    }

    private sealed record SourceLoadResult(
        List<(string Path, string Content)> Sources, ExecutorResult? Failure)
    {
        public static SourceLoadResult Loaded(List<(string Path, string Content)> sources) => new(sources, null);
        public static SourceLoadResult Failed(ExecutorResult failure) => new([], failure);
    }

    public static async Task<ExecutorResult> PublishLintAsync(
        IServerApiClient apiClient, ILogger logger, TimeProvider timeProvider, string target,
        IReadOnlyDictionary<string, string> envVars,
        Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
    {
        var context = await PipelinePublicationContext.CreateAsync(envVars, timeProvider, onOutput).ConfigureAwait(false);
        if (context is null) return new ExecutorResult(-1, false);
        var (runId, stageName, baseDirectory, startedAt) = context;
        var patterns = PipelineTargetPatterns.ParseOptional(target);
        patterns ??= ["**/*.sarif"];
        // A control plane older than analysis_category sends nothing: code quality, as before.
        if (!LintAnalysisCategories.TryParse(envVars.GetValueOrDefault(LintAnalysisCategories.VariableName), out var category))
        {
            await onOutput($"Unknown lint category '{envVars[LintAnalysisCategories.VariableName]}'.", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        var files = patterns
            .SelectMany(pattern => WorkspaceFileMatcher.GetMatchingFiles(baseDirectory, pattern))
            .Where(File.Exists)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(file => file, StringComparer.Ordinal)
            .ToList();
        foreach (var file in files)
        {
            var sarif = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
            var relativePath = Path.GetRelativePath(baseDirectory, file);
            await onOutput($"Publishing lint report from {relativePath}...", TaskLogLevel.Info).ConfigureAwait(false);
            try
            {
                // The lint store is the code-quality view; accessibility findings live only as analysis.
                if (category == AnalysisCategory.CodeQuality)
                    await apiClient.PublishLintAsync(runId, sarif, stageName, relativePath, ct).ConfigureAwait(false);
                var analysis = await SarifAnalysisReportPublisher.PublishAsync(
                    apiClient, timeProvider,
                    new SarifAnalysisReportPublisher.Publication(
                        runId, stageName, relativePath, file, "analysis-pipeline-sarif-lint",
                        "pipeline-sarif-lint",
                        category == AnalysisCategory.CodeQuality ? "Pipeline SARIF Lint" : $"Pipeline SARIF {category}",
                        category, startedAt),
                    sarif, ct).ConfigureAwait(false);
                if (analysis is null)
                {
                    await onOutput("Lint analysis publication returned no result.", TaskLogLevel.Error).ConfigureAwait(false);
                    return new ExecutorResult(1, false);
                }
                if (analysis.GateStatus is AnalysisGateStatus.Blocked or AnalysisGateStatus.Error)
                    await onOutput($"Lint verdict deferred to the common gate: {analysis.GateStatus}.", TaskLogLevel.Warning).ConfigureAwait(false);
                await onOutput($"Lint report published successfully: {relativePath}", TaskLogLevel.Info).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to publish lint report for run {RunId}", runId);
                await onOutput($"Lint publish failed: {ex.Message}", TaskLogLevel.Error).ConfigureAwait(false);
                return new ExecutorResult(-1, false);
            }
        }

        if (files.Count > 0)
            return new ExecutorResult(0, false);

        await onOutput("No SARIF files found matching patterns - failing (lint step published nothing).", TaskLogLevel.Error).ConfigureAwait(false);
        return new ExecutorResult(1, false);
    }

    private static IEnumerable<string> EnumerateCsFiles(string baseDirectory)
    {
        if (!Directory.Exists(baseDirectory)) return [];
        return Directory.EnumerateFiles(baseDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
                && !file.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)
                && !PathHasSkippedSegment(file, baseDirectory));
    }

    private static bool PathHasSkippedSegment(string file, string baseDirectory)
    {
        var relativePath = Path.GetRelativePath(baseDirectory, file);
        var segments = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(segment => s_skipDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase));
    }
}
