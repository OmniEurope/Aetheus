// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// Turns the JSON findings block an AI step was asked for (<c>findings: true</c>) into a SARIF 2.1.0
/// document and publishes it as an analysis report under the scanner key <see cref="ScannerKey"/>, so
/// its findings appear in the run's Gate tab. No grade comes of it unless the run's own YAML declares
/// an <c>analysis-gate</c>.
///
/// A missing or malformed block, or a failed publication, is reported. On an advisory step it is a
/// warning and the step's outcome is unchanged; otherwise the step fails, because the publication it
/// declared did not happen.
/// </summary>
internal static class AiFindingsPublisher
{
    internal const string ScannerKey = "ai-review";
    internal const string ArtifactName = "analysis-ai-review";
    internal const string SarifFileName = "ai-review.sarif";
    internal const int MaxFindings = 500;

    /// <summary>True when the findings were published, or when they were not and the step is advisory.</summary>
    public static async Task<bool> PublishAsync(
        IServerApiClient apiClient,
        TimeProvider timeProvider,
        ILogger logger,
        AiRunConfiguration configuration,
        string answer,
        string temporaryDirectory,
        DateTime startedAt,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken ct)
    {
        if (!configuration.PublishFindings || configuration.PipelineRunId is null) return true;
        var problem = await TryPublishAsync(
            apiClient, timeProvider, logger, configuration, answer, temporaryDirectory, startedAt, onOutput, ct)
            .ConfigureAwait(false);
        if (problem is null) return true;
        await onOutput(
            configuration.Advisory ? $"AI findings not published (advisory step, ignored): {problem}" : $"AI findings not published: {problem}",
            configuration.Advisory ? TaskLogLevel.Warning : TaskLogLevel.Error).ConfigureAwait(false);
        return configuration.Advisory;
    }

    private static async Task<string?> TryPublishAsync(
        IServerApiClient apiClient,
        TimeProvider timeProvider,
        ILogger logger,
        AiRunConfiguration configuration,
        string answer,
        string temporaryDirectory,
        DateTime startedAt,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken ct)
    {
        var conversion = ToSarif(answer, configuration.FindingsPlaceholder);
        if (conversion.Error is not null) return conversion.Error;
        var path = Path.Combine(temporaryDirectory, SarifFileName);
        await File.WriteAllTextAsync(path, conversion.Sarif, ct).ConfigureAwait(false);
        try
        {
            var analysis = await SarifAnalysisReportPublisher.PublishAsync(
                apiClient, timeProvider,
                new SarifAnalysisReportPublisher.Publication(
                    configuration.PipelineRunId!.Value, configuration.StageName, SarifFileName,
                    path, ArtifactName, ScannerKey, $"AI review ({configuration.ProfileName})",
                    AnalysisCategory.CodeQuality, startedAt),
                conversion.Sarif!, ct).ConfigureAwait(false);
            if (analysis is null) return "the control plane returned no report.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Mandatory: an advisory step's publication failing must not fail the step.
            logger.LogWarning(ex, "AI findings publication failed for run {RunId}", configuration.PipelineRunId);
            return ex.Message;
        }
        var skipped = conversion.Skipped > 0 ? $", {conversion.Skipped} entries without a message skipped" : string.Empty;
        await onOutput($"AI findings published: {conversion.Count} finding(s){skipped}.", TaskLogLevel.Info)
            .ConfigureAwait(false);
        return null;
    }

    internal sealed record Conversion(string? Sarif, int Count, int Skipped, string? Error);

    internal const string PlaceholderRule = "ai-review-placeholder";

    internal const string PlaceholderMessage =
        "[FAKE] Synthetic placeholder: the model reported no finding on this range. It only shows that the AI "
        + "review reached the provider and published to the Gate tab; it is not a real issue.";

    /// <summary>The SARIF of the last <c>```json</c> block of the answer, or why there is none. With
    /// <paramref name="placeholder"/>, an empty block yields one synthetic [FAKE] note instead of nothing.</summary>
    internal static Conversion ToSarif(string answer, bool placeholder = false)
    {
        var block = LastJsonBlock(answer);
        if (block is null) return new Conversion(null, 0, 0, "the answer has no ```json findings block.");
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(block);
        }
        catch (JsonException ex)
        {
            return new Conversion(null, 0, 0, $"the findings block is not valid JSON ({ex.Message}).");
        }
        var items = root switch
        {
            JsonArray array => array,
            JsonObject obj when obj["findings"] is JsonArray array => array,
            _ => null
        };
        if (items is null) return new Conversion(null, 0, 0, "the findings block holds no findings array.");
        var results = new JsonArray();
        var rules = new SortedSet<string>(StringComparer.Ordinal);
        var skipped = 0;
        foreach (var item in items.Take(MaxFindings))
        {
            if (item is not JsonObject finding || Text(finding, "message") is not { } message)
            {
                skipped++;
                continue;
            }
            var rule = Text(finding, "rule") ?? "ai-review";
            rules.Add(rule);
            var result = new JsonObject
            {
                ["ruleId"] = rule,
                ["level"] = Level(Text(finding, "severity")),
                ["message"] = new JsonObject { ["text"] = message }
            };
            if (Location(finding) is { } location) result["locations"] = new JsonArray(location);
            results.Add(result);
        }
        if (placeholder && results.Count == 0)
        {
            rules.Add(PlaceholderRule);
            results.Add(new JsonObject
            {
                ["ruleId"] = PlaceholderRule,
                ["level"] = "note",
                ["message"] = new JsonObject { ["text"] = PlaceholderMessage }
            });
        }
        var sarif = new JsonObject
        {
            ["version"] = "2.1.0",
            ["$schema"] = "https://json.schemastore.org/sarif-2.1.0.json",
            ["runs"] = new JsonArray(new JsonObject
            {
                ["tool"] = new JsonObject
                {
                    ["driver"] = new JsonObject
                    {
                        ["name"] = ScannerKey,
                        ["rules"] = new JsonArray(rules.Select(id => (JsonNode)new JsonObject { ["id"] = id }).ToArray())
                    }
                },
                ["results"] = results
            })
        };
        return new Conversion(sarif.ToJsonString(), results.Count, skipped, null);
    }

    private static string? LastJsonBlock(string answer)
    {
        var start = answer.LastIndexOf("```json", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        var contentStart = answer.IndexOf('\n', start);
        if (contentStart < 0) return null;
        var end = answer.IndexOf("```", contentStart, StringComparison.Ordinal);
        return end < 0 ? null : answer[(contentStart + 1)..end];
    }

    private static string? Text(JsonObject finding, string name) =>
        finding[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;

    private static string Level(string? severity) => severity?.ToLowerInvariant() switch
    {
        "error" => "error",
        "warning" => "warning",
        _ => "note"
    };

    /// <summary>A repository-relative file, with its line when the model gave one; anything rooted or
    /// climbing out of the repository is dropped rather than published.</summary>
    private static JsonObject? Location(JsonObject finding)
    {
        var file = Text(finding, "file")?.Replace('\\', '/');
        if (file is null
            || Path.IsPathRooted(file)
            || file.Contains(':', StringComparison.Ordinal)
            || file.Split('/').Any(segment => segment is ".." or "."))
            return null;
        var physical = new JsonObject { ["artifactLocation"] = new JsonObject { ["uri"] = file } };
        if (finding["line"] is JsonValue lineValue && lineValue.TryGetValue<int>(out var line) && line >= 1)
            physical["region"] = new JsonObject { ["startLine"] = line };
        return new JsonObject { ["physicalLocation"] = physical };
    }
}
