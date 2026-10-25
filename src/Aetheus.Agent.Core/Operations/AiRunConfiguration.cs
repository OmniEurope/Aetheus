// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text.Json;

namespace Aetheus.Agent.Core.Operations;

/// <summary>Reads the encrypted task configuration for an HTTP API profile: provider, HTTPS URL,
/// model and API key (ADR-023). Reject incomplete configuration, including CLI-runner tasks lacking
/// these fields.</summary>
internal sealed record AiRunConfiguration(
    int ServerTaskId,
    string ProfileName,
    AiProvider Provider,
    Uri BaseUrl,
    string Model,
    string ApiKey,
    AiReasoningEffort Effort,
    int MaxOutputTokens,
    string Prompt,
    string? PromptFile,
    List<string> ContextPaths,
    string WorkingDirectory,
    bool Gate,
    bool SendsDataExternally,
    int MaxOutputBytes,
    int? SourceRepositoryId,
    string? SourceRepositoryPath,
    string? SourceRef,
    string? GitUsername,
    string? GitPassword)
{
    internal const string IncompleteError =
        "AI run configuration is incomplete: the task names no API provider, model or key (a profile of the retired CLI runner must be reconfigured).";

    /// <summary>An advisory step's failures are reported, never decisive (PipelineStepDefinition.Advisory).</summary>
    public bool Advisory { get; init; }

    /// <summary>The model is asked for a JSON findings block, published as SARIF under the run.</summary>
    public bool PublishFindings { get; init; }

    /// <summary>An empty findings block publishes one synthetic, [FAKE]-marked note instead.</summary>
    public bool FindingsPlaceholder { get; init; }

    /// <summary>A commit range whose log and diff are added to the prompt, as the run substituted it.</summary>
    public string? GitRange { get; init; }

    /// <summary>Run and stage the findings are published under; set together with <see cref="PublishFindings"/>.</summary>
    public int? PipelineRunId { get; init; }

    public string? StageName { get; init; }

    /// <summary>Which profile a pipeline step runs with and why, as the backend resolved it (the step's
    /// name, the pipeline's profile, or the most preferred usable one); written at the top of the step
    /// log. Null for a standalone task, which names its profile itself.</summary>
    public string? ProfileResolution { get; init; }

    /// <summary>Never prints the API key nor the Git password (a record would print every member).</summary>
    public override string ToString() =>
        $"AiRunConfiguration {{ Task = {ServerTaskId}, Profile = {ProfileName}, Provider = {Provider}, Model = {Model} }}";

    public static bool TryRead(
        IReadOnlyDictionary<string, string> env,
        out AiRunConfiguration configuration,
        out string error)
    {
        configuration = default!;
        error = IncompleteError;
        if (!TryReadRequired(env, out var required)) return false;
        var contextPaths = ReadJsonList(env.GetValueOrDefault("AETHEUS_AI_CONTEXT_PATHS_JSON"));
        if (!TryReadSourceRepository(
                env, out var sourceRepositoryId, out var sourceRepositoryPath, out var sourceRef))
            return false;
        var publishFindings = IsTrue(env, "AETHEUS_AI_FINDINGS");
        int? runId = int.TryParse(env.GetValueOrDefault("AETHEUS_RUN_ID"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedRunId)
            ? parsedRunId
            : null;
        if (publishFindings && runId is null)
        {
            error = "AI findings need the pipeline run they are published under (AETHEUS_RUN_ID).";
            return false;
        }
        var gitRange = env.GetValueOrDefault("AETHEUS_AI_GIT_RANGE");
        configuration = new AiRunConfiguration(
            required.TaskId,
            required.ProfileName,
            required.Provider,
            required.BaseUrl,
            required.Model,
            required.ApiKey,
            AiEffortTokens.Parse(env.GetValueOrDefault("AETHEUS_AI_EFFORT")),
            required.MaxOutputTokens,
            required.Prompt,
            env.GetValueOrDefault("AETHEUS_AI_PROMPT_FILE"),
            contextPaths,
            env.GetValueOrDefault("AETHEUS_AI_WORKING_DIR") ?? string.Empty,
            IsTrue(env, "AETHEUS_AI_GATE"),
            IsTrue(env, "AETHEUS_AI_SENDS_DATA_EXTERNALLY"),
            Math.Clamp(required.MaxOutputBytes, 1024, 1_000_000),
            sourceRepositoryId,
            sourceRepositoryPath,
            sourceRef,
            env.GetValueOrDefault("GIT_USERNAME"),
            env.GetValueOrDefault("GIT_PASSWORD"))
        {
            Advisory = IsTrue(env, "AETHEUS_AI_ADVISORY"),
            PublishFindings = publishFindings,
            FindingsPlaceholder = publishFindings && IsTrue(env, "AETHEUS_AI_FINDINGS_PLACEHOLDER"),
            GitRange = string.IsNullOrWhiteSpace(gitRange) ? null : gitRange.Trim(),
            PipelineRunId = runId,
            StageName = env.GetValueOrDefault("AETHEUS_STAGE_NAME"),
            ProfileResolution = env.GetValueOrDefault("AETHEUS_AI_PROFILE_RESOLUTION") is { Length: > 0 } resolution
                ? resolution
                : null
        };
        return true;
    }

    private static bool IsTrue(IReadOnlyDictionary<string, string> env, string name) =>
        string.Equals(env.GetValueOrDefault(name), "true", StringComparison.OrdinalIgnoreCase);

    private static bool TryReadRequired(
        IReadOnlyDictionary<string, string> env, out RequiredAiConfiguration configuration)
    {
        configuration = default!;
        if (!env.TryGetValue("AETHEUS_TASK_ID", out var taskText)
            || !int.TryParse(taskText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var taskId)
            || !env.TryGetValue("AETHEUS_AI_PROFILE_NAME", out var profileName)
            || !AiProviderCatalog.TryParse(env.GetValueOrDefault("AETHEUS_AI_PROVIDER"), out var provider)
            || !AiProviderCatalog.IsValidBaseUrl(env.GetValueOrDefault("AETHEUS_AI_BASE_URL"), out var baseUrl)
            || !env.TryGetValue("AETHEUS_AI_MODEL", out var model) || string.IsNullOrWhiteSpace(model)
            || !env.TryGetValue("AETHEUS_AI_API_KEY", out var apiKey) || string.IsNullOrWhiteSpace(apiKey)
            || !env.TryGetValue("AETHEUS_AI_PROMPT", out var prompt)
            || !env.TryGetValue("AETHEUS_AI_MAX_OUTPUT_BYTES", out var maxText)
            || !int.TryParse(maxText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxOutputBytes))
            return false;
        var maxOutputTokens = int.TryParse(
            env.GetValueOrDefault("AETHEUS_AI_MAX_OUTPUT_TOKENS"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var tokens)
            && tokens > 0
                ? Math.Clamp(tokens, 256, 128_000)
                : 16_000;
        configuration = new RequiredAiConfiguration(
            taskId, profileName, provider, baseUrl!, model.Trim(), apiKey, prompt, maxOutputBytes, maxOutputTokens);
        return true;
    }

    private static List<string> ReadJsonList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    private sealed record RequiredAiConfiguration(
        int TaskId, string ProfileName, AiProvider Provider, Uri BaseUrl, string Model, string ApiKey,
        string Prompt, int MaxOutputBytes, int MaxOutputTokens)
    {
        public override string ToString() => $"RequiredAiConfiguration {{ Task = {TaskId} }}";
    }

    private static bool TryReadSourceRepository(
        IReadOnlyDictionary<string, string> env,
        out int? sourceRepositoryId,
        out string? sourceRepositoryPath,
        out string? sourceRef)
    {
        sourceRepositoryId = null;
        sourceRepositoryPath = null;
        sourceRef = null;
        var hasSourceRepositoryId = env.TryGetValue(
            "AETHEUS_AI_SOURCE_REPOSITORY_ID",
            out var sourceRepositoryText);
        var hasSourceRepositoryPath = env.TryGetValue(
            "AETHEUS_AI_SOURCE_REPOSITORY_PATH",
            out sourceRepositoryPath);
        if (hasSourceRepositoryId || hasSourceRepositoryPath)
        {
            if (!hasSourceRepositoryId
                || !hasSourceRepositoryPath
                || !int.TryParse(sourceRepositoryText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedSourceRepositoryId)
                || parsedSourceRepositoryId <= 0
                || string.IsNullOrWhiteSpace(sourceRepositoryPath)
                || !sourceRepositoryPath.StartsWith("/git/", StringComparison.Ordinal)
                || sourceRepositoryPath.Contains("..", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(env.GetValueOrDefault("GIT_USERNAME"))
                || string.IsNullOrWhiteSpace(env.GetValueOrDefault("GIT_PASSWORD")))
                return false;
            sourceRepositoryId = parsedSourceRepositoryId;
            sourceRef = env.GetValueOrDefault("AETHEUS_AI_SOURCE_REF");
        }
        return true;
    }
}
