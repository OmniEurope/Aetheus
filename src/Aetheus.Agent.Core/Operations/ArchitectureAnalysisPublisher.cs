// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Aetheus.Agent.Core.Operations;

internal static class ArchitectureAnalysisPublisher
{
    public static async Task<ExecutorResult> PublishAsync(
        IServerApiClient apiClient,
        TimeProvider timeProvider,
        int runId,
        string? stageName,
        string baseDirectory,
        IReadOnlyList<ParsedCSharpSource> sources,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken ct)
    {
        var startedAt = timeProvider.GetUtcNow().UtcDateTime;
        // Publish each architecture-analysis phase duration as a run metric to locate slow
        // processing.
        var phase = Stopwatch.StartNew();
        async Task PhaseAsync(string key)
        {
            await onOutput(
                $"##aetheus[pipelinemetric key=architecture.publish.{key};type=Duration;unit=s]"
                + phase.Elapsed.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture),
                TaskLogLevel.Info).ConfigureAwait(false);
            phase.Restart();
        }
        var rulesPath = Path.Combine(baseDirectory, ".aetheus", "architecture-rules.json");
        var productSources = SelectProductSources(baseDirectory, sources);
        var report = ArchitectureAnalyzer.Analyze(productSources, rulesPath);
        var metrics = report.Graphs.SelectMany(graph => graph.Value.Edges.Select(edge => new
        {
            key = $"architecture.dependencies.{graph.Key}",
            value = (double)edge.Count,
            unit = "references",
            scope = $"{graph.Key}-edge",
            language = "csharp",
            symbol = (string?)$"{edge.Source}->{edge.Target}",
            toolName = "Microsoft.CodeAnalysis",
            direction = "LowerIsBetter"
        }))
            .Concat(report.Edges.Select(edge => new
            {
                key = "architecture.dependencies",
                value = (double)edge.Count,
                unit = "references",
                scope = "namespace-edge",
                language = "csharp",
                symbol = (string?)$"{edge.Source}->{edge.Target}",
                toolName = "Microsoft.CodeAnalysis",
                direction = "LowerIsBetter"
            }))
            .Concat(report.Graphs.SelectMany(graph => graph.Value.Instability.Select(item => new
            {
                key = $"architecture.instability.{graph.Key}",
                value = item.Value,
                unit = "ratio",
                scope = graph.Key,
                language = "csharp",
                symbol = (string?)item.Key,
                toolName = "Microsoft.CodeAnalysis",
                direction = "LowerIsBetter"
            })))
            .Concat(report.Graphs.Select(graph => new
            {
                key = $"architecture.cycles.{graph.Key}",
                value = (double)graph.Value.Cycles.Count,
                unit = "cycles",
                scope = graph.Key,
                language = "csharp",
                symbol = (string?)null,
                toolName = "Microsoft.CodeAnalysis",
                direction = "LowerIsBetter"
            }))
            .Concat([
                new
                {
                    key = "architecture.cycles",
                    value = (double)report.Graphs["project"].Cycles.Count,
                    unit = "cycles",
                    scope = "project",
                    language = "csharp",
                    symbol = (string?)null,
                    toolName = "Microsoft.CodeAnalysis",
                    direction = "LowerIsBetter"
                }
            ])
            .ToList();
        var metricsJson = JsonSerializer.Serialize(new { metrics });
        await PhaseAsync("analyze").ConfigureAwait(false);
        await onOutput(
            $"Publishing architecture metrics report: {metrics.Count} records, {System.Text.Encoding.UTF8.GetByteCount(metricsJson)} bytes.",
            TaskLogLevel.Info).ConfigureAwait(false);
        var metricsArtifact = await AnalysisArtifactUploader.UploadTextAsync(apiClient, runId,
            "analysis-dotnet-architecture-metrics", stageName, "dotnet-architecture-metrics.json", metricsJson, ct).ConfigureAwait(false);
        await PhaseAsync("metrics_artifact").ConfigureAwait(false);
        var metricsResult = await apiClient.PublishAnalysisReportAsync(runId, new PublishAnalysisReportRequest
        {
            ScannerKey = "dotnet-architecture-metrics",
            ScannerName = "Microsoft.CodeAnalysis Architecture",
            ScannerVersion = typeof(Microsoft.CodeAnalysis.Compilation).Assembly.GetName().Version?.ToString() ?? "unknown",
            Category = AnalysisCategory.Architecture,
            Status = AnalysisReportStatus.Passed,
            Format = AnalysisReportFormat.MetricsJson,
            ReportContent = metricsJson,
            ReportPath = "dotnet-architecture-metrics.json",
            PipelineArtifactId = metricsArtifact,
            StageName = stageName,
            RuleSetHash = await HashRulesAsync(rulesPath, ct).ConfigureAwait(false),
            StartedAt = startedAt,
            CompletedAt = timeProvider.GetUtcNow().UtcDateTime
        }, ct).ConfigureAwait(false);
        await PhaseAsync("metrics_report").ConfigureAwait(false);
        if (metricsResult is null)
            return new ExecutorResult(1, false);

        var sarif = BuildSarif(report);
        await onOutput(
            $"Publishing architecture findings report: {report.Violations.Count} violations, {System.Text.Encoding.UTF8.GetByteCount(sarif)} bytes.",
            TaskLogLevel.Info).ConfigureAwait(false);
        var findingsArtifact = await AnalysisArtifactUploader.UploadTextAsync(apiClient, runId,
            "analysis-dotnet-architecture-findings", stageName, "dotnet-architecture.sarif", sarif, ct).ConfigureAwait(false);
        var findingsResult = await apiClient.PublishAnalysisReportAsync(runId, new PublishAnalysisReportRequest
        {
            ScannerKey = "dotnet-architecture",
            ScannerName = "Microsoft.CodeAnalysis Architecture",
            ScannerVersion = typeof(Microsoft.CodeAnalysis.Compilation).Assembly.GetName().Version?.ToString() ?? "unknown",
            Category = AnalysisCategory.Architecture,
            Status = report.Violations.Count == 0 ? AnalysisReportStatus.Passed : AnalysisReportStatus.Failed,
            Format = AnalysisReportFormat.Sarif,
            ReportContent = sarif,
            ReportPath = "dotnet-architecture.sarif",
            PipelineArtifactId = findingsArtifact,
            StageName = stageName,
            RuleSetHash = await HashRulesAsync(rulesPath, ct).ConfigureAwait(false),
            StartedAt = startedAt,
            CompletedAt = timeProvider.GetUtcNow().UtcDateTime
        }, ct).ConfigureAwait(false);
        await PhaseAsync("findings").ConfigureAwait(false);
        await onOutput(
            $"Architecture graphs published: {string.Join(", ", report.Graphs.Select(item => $"{item.Key}={item.Value.Edges.Count} edges"))}; {report.Graphs["project"].Cycles.Count} project cycles, {report.Violations.Count} violations.",
            findingsResult?.GateStatus == AnalysisGateStatus.Blocked ? TaskLogLevel.Error : TaskLogLevel.Info).ConfigureAwait(false);
        return findingsResult is null ? new ExecutorResult(1, false) : new ExecutorResult(0, false);
    }

    internal static IReadOnlyList<ParsedCSharpSource> SelectProductSources(
        string baseDirectory,
        IEnumerable<ParsedCSharpSource> sources) =>
        sources.Where(source => IsProductSource(baseDirectory, source.Path)).ToList();

    internal static IReadOnlyList<(string Path, string Content)> SelectProductSources(
        string baseDirectory,
        IEnumerable<(string Path, string Content)> sources) =>
        sources.Where(source => IsProductSource(baseDirectory, source.Path)).ToList();

    private static bool IsProductSource(string baseDirectory, string sourcePath)
    {
        var separator = Path.DirectorySeparatorChar;
        var relative = Path.GetRelativePath(baseDirectory, sourcePath);
        return !Path.IsPathRooted(relative)
            && !relative.StartsWith("..", StringComparison.Ordinal)
            && relative.StartsWith($"src{separator}", StringComparison.OrdinalIgnoreCase)
            && !relative.Contains(
                $"{separator}Data{separator}Migrations{separator}",
                StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildSarif(ArchitectureReport report)
    {
        var rules = report.Violations.Select(item => item.RuleId).Distinct(StringComparer.Ordinal)
            .Select(id => new
            {
                id,
                shortDescription = new
                {
                    text = id.StartsWith("architecture-cycle", StringComparison.Ordinal)
                        ? "Architecture dependency cycle"
                        : "Forbidden dependency"
                }
            });
        var results = report.Violations.Select(item => new
        {
            ruleId = item.RuleId,
            level = "warning",
            message = new { text = item.Message },
            properties = new { severity = "Medium", confidence = "High" },
            logicalLocations = new[] { new { fullyQualifiedName = $"{item.Source}->{item.Target}" } }
        });
        return JsonSerializer.Serialize(new
        {
            version = "2.1.0",
            runs = new[]
            {
                new
                {
                    tool = new { driver = new { name = "Microsoft.CodeAnalysis Architecture", rules } },
                    results
                }
            }
        });
    }

    private static async Task<string?> HashRulesAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
    }
}
