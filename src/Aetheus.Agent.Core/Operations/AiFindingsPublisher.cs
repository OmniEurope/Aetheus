// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;

namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// Publishes the SARIF an AI step was asked to write (<c>findings_sarif</c>) as an analysis report under
/// the scanner key <see cref="ScannerKey"/>, so its findings appear in the run's Gate tab. No grade comes
/// of it unless the run's own YAML declares an <c>analysis-gate</c>.
///
/// A file the CLI did not write, or one that is not SARIF, is reported. On an advisory step it is a
/// warning and the step's outcome is unchanged; otherwise the step fails, because the publication it
/// declared did not happen.
/// </summary>
internal static class AiFindingsPublisher
{
    internal const string ScannerKey = "ai-review";
    internal const string ArtifactName = "analysis-ai-review";

    /// <summary>True when the findings were published, or when they were not and the step is advisory.</summary>
    public static async Task<bool> PublishAsync(
        IServerApiClient apiClient,
        TimeProvider timeProvider,
        ILogger logger,
        AiRunConfiguration configuration,
        string workingDirectory,
        DateTime startedAt,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken ct)
    {
        if (configuration.FindingsSarif is null || configuration.PipelineRunId is null) return true;
        var problem = await TryPublishAsync(
            apiClient, timeProvider, logger, configuration, workingDirectory, startedAt, onOutput, ct)
            .ConfigureAwait(false);
        if (problem is null) return true;
        await onOutput(
            configuration.Advisory ? $"AI findings not published (advisory step, ignored): {problem}" : $"AI findings not published: {problem}",
            configuration.Advisory ? TaskLogLevel.Warning : TaskLogLevel.Error).ConfigureAwait(false);
        return configuration.Advisory;
    }

    /// <summary>Removes a findings file left in the workspace before the CLI runs, so a file the CLI did
    /// not write this time is never published as its findings.</summary>
    public static void DeleteStale(AiRunConfiguration configuration, string workingDirectory)
    {
        var path = ResolvePath(configuration, workingDirectory);
        if (path is not null && File.Exists(path)) File.Delete(path);
    }

    private static async Task<string?> TryPublishAsync(
        IServerApiClient apiClient,
        TimeProvider timeProvider,
        ILogger logger,
        AiRunConfiguration configuration,
        string workingDirectory,
        DateTime startedAt,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken ct)
    {
        var path = ResolvePath(configuration, workingDirectory);
        if (path is null) return $"'{configuration.FindingsSarif}' is not a path inside the workspace.";
        if (!File.Exists(path)) return $"the CLI wrote no '{configuration.FindingsSarif}'.";
        if (new FileInfo(path).Length > configuration.MaxOutputBytes)
            return $"'{configuration.FindingsSarif}' exceeds the profile's {configuration.MaxOutputBytes} output bytes.";
        var sarif = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        if (!LooksLikeSarif(sarif)) return $"'{configuration.FindingsSarif}' is not a SARIF document (version and runs).";
        try
        {
            var analysis = await SarifAnalysisReportPublisher.PublishAsync(
                apiClient, timeProvider,
                new SarifAnalysisReportPublisher.Publication(
                    configuration.PipelineRunId!.Value, configuration.StageName, configuration.FindingsSarif!,
                    path, ArtifactName, ScannerKey, $"AI review ({configuration.ProfileName})",
                    AnalysisCategory.CodeQuality, startedAt),
                sarif, ct).ConfigureAwait(false);
            if (analysis is null) return "the control plane returned no report.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Mandatory: an advisory step's publication failing must not fail the step.
            logger.LogWarning(ex, "AI findings publication failed for run {RunId}", configuration.PipelineRunId);
            return ex.Message;
        }
        await onOutput($"AI findings published: {configuration.FindingsSarif}", TaskLogLevel.Info).ConfigureAwait(false);
        return null;
    }

    private static string? ResolvePath(AiRunConfiguration configuration, string workingDirectory)
    {
        if (configuration.FindingsSarif is null) return null;
        var root = Path.GetFullPath(workingDirectory);
        var full = Path.GetFullPath(Path.Combine(root, configuration.FindingsSarif));
        return full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            ? full
            : null;
    }

    internal static bool LooksLikeSarif(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String
                && root.TryGetProperty("runs", out var runs) && runs.ValueKind == JsonValueKind.Array;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
