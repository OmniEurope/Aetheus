// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text.Json;

namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// ADR-056, "every detected problem is a finding": every method over the complexity threshold is a finding
/// of its own, published as SARIF next to the complexity metrics, instead of a count the gate compares to a
/// limit. The finding is the method (its qualified symbol and its file), never its line or its complexity,
/// so it stays the same finding while the method stays too complex and is fixed when it no longer is.
/// </summary>
internal static class ComplexityFindingsPublisher
{
    internal const string ScannerKey = "roslyn-complexity";
    internal const string RuleId = "complexity-method";

    public static async Task<AnalysisReportDto?> PublishAsync(
        IServerApiClient apiClient,
        TimeProvider timeProvider,
        int runId,
        string? stageName,
        string baseDirectory,
        IReadOnlyList<ComplexityMethod> methods,
        DateTime startedAt,
        CancellationToken ct)
    {
        var sarif = BuildSarif(baseDirectory, methods);
        var artifactId = await AnalysisArtifactUploader.UploadTextAsync(apiClient, runId,
            "analysis-roslyn-complexity", stageName, "roslyn-complexity.sarif", sarif, ct).ConfigureAwait(false);
        return await apiClient.PublishAnalysisReportAsync(runId, new PublishAnalysisReportRequest
        {
            ScannerKey = ScannerKey,
            ScannerName = "Microsoft.CodeAnalysis Complexity",
            ScannerVersion = typeof(Microsoft.CodeAnalysis.SyntaxTree).Assembly.GetName().Version?.ToString() ?? "unknown",
            Category = AnalysisCategory.CodeQuality,
            Status = methods.Count == 0 ? AnalysisReportStatus.Passed : AnalysisReportStatus.Failed,
            Format = AnalysisReportFormat.Sarif,
            ReportContent = sarif,
            ReportPath = "roslyn-complexity.sarif",
            PipelineArtifactId = artifactId,
            StageName = stageName,
            StartedAt = startedAt,
            CompletedAt = timeProvider.GetUtcNow().UtcDateTime
        }, ct).ConfigureAwait(false);
    }

    internal static string BuildSarif(string baseDirectory, IReadOnlyList<ComplexityMethod> methods)
    {
        var results = methods.Select(method =>
        {
            var relative = RelativePath(baseDirectory, method.Path);
            return new Dictionary<string, object>
            {
                ["ruleId"] = RuleId,
                ["level"] = "warning",
                ["message"] = new
                {
                    text = $"{method.Symbol} has a cyclomatic complexity of "
                        + $"{method.Cyclomatic.ToString(CultureInfo.InvariantCulture)}, "
                        + $"above the threshold of {ComplexityAnalyzer.HighComplexityThreshold}."
                },
                ["properties"] = new { severity = "Medium", confidence = "High" },
                // The method in its file, never the complexity or the line, which change without the method
                // becoming another one.
                ["partialFingerprints"] = new Dictionary<string, string>
                {
                    ["aetheus/identity"] = $"{RuleId}|{relative.ToLowerInvariant()}|{method.Symbol}"
                },
                ["locations"] = new[]
                {
                    new
                    {
                        physicalLocation = new
                        {
                            artifactLocation = new { uri = relative },
                            region = new { startLine = method.Line }
                        },
                        logicalLocations = new[] { new { fullyQualifiedName = method.Symbol } }
                    }
                }
            };
        });
        return JsonSerializer.Serialize(new
        {
            version = "2.1.0",
            runs = new[]
            {
                new
                {
                    tool = new
                    {
                        driver = new
                        {
                            name = "Microsoft.CodeAnalysis Complexity",
                            rules = new[]
                            {
                                new { id = RuleId, shortDescription = new { text = "Method over the cyclomatic complexity threshold" } }
                            }
                        }
                    },
                    results
                }
            }
        });
    }

    private static string RelativePath(string baseDirectory, string path) =>
        Path.GetRelativePath(baseDirectory, path).Replace('\\', '/');
}
