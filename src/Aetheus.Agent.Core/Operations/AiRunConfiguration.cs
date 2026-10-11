// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;

namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// What an <c>OperationKind.AiRun</c> task asks of the agent, read from the task environment the
/// control plane built. Extracted from <see cref="AiRunOperationExecutor"/>, which runs it.
/// </summary>
internal sealed record AiRunConfiguration(
    int ServerTaskId,
    string ProfileName,
    string Binary,
    List<string> Arguments,
    string Prompt,
    string? PromptFile,
    List<string> ContextPaths,
    string WorkingDirectory,
    bool Gate,
    bool SendsDataExternally,
    int MaxOutputBytes,
    Dictionary<string, string> RunnerEnvironment,
    int? SourceRepositoryId,
    string? SourceRepositoryPath,
    string? SourceRef,
    string? GitUsername,
    string? GitPassword)
{
    /// <summary>An advisory step's failures are reported, never decisive (PipelineStepDefinition.Advisory).</summary>
    public bool Advisory { get; init; }

    /// <summary>False skips the workspace diff capture after the CLI exits.</summary>
    public bool CaptureDiff { get; init; } = true;

    /// <summary>Workspace-relative SARIF the CLI writes its findings to, published after it exits.</summary>
    public string? FindingsSarif { get; init; }

    /// <summary>Run and stage the findings are published under; set together with <see cref="FindingsSarif"/>.</summary>
    public int? PipelineRunId { get; init; }

    public string? StageName { get; init; }

    public static bool TryRead(
        IReadOnlyDictionary<string, string> env,
        out AiRunConfiguration configuration,
        out string error)
    {
        configuration = default!;
        error = "AI run configuration is incomplete.";
        if (!TryReadRequired(env, out var required)) return false;
        List<string>? arguments;
        try
        {
            arguments = JsonSerializer.Deserialize<List<string>>(required.ArgumentsJson);
        }
        catch (JsonException)
        {
            return false;
        }
        if (arguments is null || arguments.Count > 64) return false;
        var contextPaths = ReadJsonList(env.GetValueOrDefault("AETHEUS_AI_CONTEXT_PATHS_JSON"));
        if (!TryReadSourceRepository(
                env, out var sourceRepositoryId, out var sourceRepositoryPath, out var sourceRef))
            return false;
        var findingsSarif = env.GetValueOrDefault("AETHEUS_AI_FINDINGS_SARIF");
        int? runId = int.TryParse(env.GetValueOrDefault("AETHEUS_RUN_ID"), out var parsedRunId) ? parsedRunId : null;
        if (!string.IsNullOrWhiteSpace(findingsSarif) && runId is null)
        {
            error = "AI findings_sarif needs the pipeline run it is published under (AETHEUS_RUN_ID).";
            return false;
        }
        configuration = new AiRunConfiguration(
            required.TaskId,
            required.ProfileName,
            required.Binary,
            arguments,
            required.Prompt,
            env.GetValueOrDefault("AETHEUS_AI_PROMPT_FILE"),
            contextPaths,
            env.GetValueOrDefault("AETHEUS_AI_WORKING_DIR") ?? string.Empty,
            IsTrue(env, "AETHEUS_AI_GATE"),
            IsTrue(env, "AETHEUS_AI_SENDS_DATA_EXTERNALLY"),
            Math.Clamp(required.MaxOutputBytes, 1024, 1_000_000),
            ReadRunnerEnvironment(env, Environment.GetEnvironmentVariable, OperatingSystem.IsWindows()),
            sourceRepositoryId,
            sourceRepositoryPath,
            sourceRef,
            env.GetValueOrDefault("GIT_USERNAME"),
            env.GetValueOrDefault("GIT_PASSWORD"))
        {
            Advisory = IsTrue(env, "AETHEUS_AI_ADVISORY"),
            CaptureDiff = !string.Equals(
                env.GetValueOrDefault("AETHEUS_AI_CAPTURE_DIFF"), "false", StringComparison.OrdinalIgnoreCase),
            FindingsSarif = string.IsNullOrWhiteSpace(findingsSarif) ? null : findingsSarif,
            PipelineRunId = runId,
            StageName = env.GetValueOrDefault("AETHEUS_STAGE_NAME")
        };
        return true;
    }

    /// <summary>
    /// The CLI's environment: the profile's variables, never the executor's AETHEUS_* nor the ephemeral
    /// Git credentials, plus the home directory of the agent's own account. The process starts with an
    /// emptied environment (DefaultShellRunner keeps PATH, TEMP and locale only), and a CLI logged in once
    /// headless keeps its login and configuration under that home (Claude Code: ~/.claude, or
    /// %USERPROFILE%\.claude on Windows). A profile that sets the variable itself wins.
    /// </summary>
    internal static Dictionary<string, string> ReadRunnerEnvironment(
        IReadOnlyDictionary<string, string> env, Func<string, string?> agentVariable, bool isWindows)
    {
        var runner = env.Where(variable =>
                !variable.Key.StartsWith("AETHEUS_", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(variable.Key, "GIT_USERNAME", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(variable.Key, "GIT_PASSWORD", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(variable => variable.Key, variable => variable.Value, StringComparer.Ordinal);
        string[] homeVariables = isWindows ? ["HOME", "USERPROFILE"] : ["HOME"];
        foreach (var name in homeVariables)
        {
            var value = agentVariable(name);
            if (!string.IsNullOrWhiteSpace(value)) runner.TryAdd(name, value);
        }
        return runner;
    }

    private static bool IsTrue(IReadOnlyDictionary<string, string> env, string name) =>
        string.Equals(env.GetValueOrDefault(name), "true", StringComparison.OrdinalIgnoreCase);

    private static bool TryReadRequired(
        IReadOnlyDictionary<string, string> env, out RequiredAiConfiguration configuration)
    {
        configuration = default!;
        if (!env.TryGetValue("AETHEUS_TASK_ID", out var taskText)
            || !int.TryParse(taskText, out var taskId)
            || !env.TryGetValue("AETHEUS_AI_PROFILE_NAME", out var profileName)
            || !env.TryGetValue("AETHEUS_AI_BINARY", out var binary)
            || !env.TryGetValue("AETHEUS_AI_ARGS_JSON", out var argsJson)
            || !env.TryGetValue("AETHEUS_AI_PROMPT", out var prompt)
            || !env.TryGetValue("AETHEUS_AI_MAX_OUTPUT_BYTES", out var maxText)
            || !int.TryParse(maxText, out var maxOutputBytes)) return false;
        configuration = new RequiredAiConfiguration(
            taskId, profileName, binary, argsJson, prompt, maxOutputBytes);
        return true;
    }

    private static List<string> ReadJsonList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    private sealed record RequiredAiConfiguration(
        int TaskId, string ProfileName, string Binary, string ArgumentsJson, string Prompt, int MaxOutputBytes);

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
                || !int.TryParse(sourceRepositoryText, out var parsedSourceRepositoryId)
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
