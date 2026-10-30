// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using System.Text.RegularExpressions;
using Aetheus.Back.Components.Monitoring;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Stateless helpers for the pipeline-run engine (<see cref="PipelineRunService"/>): variable
/// substitution/parsing, condition evaluation, matrix expansion, isolation-policy checks and the
/// per-step env scoping. DTO mapping (run list projection / detail mapping) lives in the sibling
/// <see cref="PipelineRunDtoMapper"/>. Extracted from the former <c>PipelineRunService.Helpers.cs</c>
/// / <c>.Tasks.cs</c> partials into a real collaborator. <c>partial</c> here is required only for the
/// <c>[GeneratedRegex]</c> source generator.
/// </summary>
public static partial class PipelineRunHelpers
{
    internal const string SystemPrepareStage = "System:Prepare";
    internal const string SystemCleanupStage = PipelineSystemStages.Cleanup;
    internal const string CancellationRequestedVariable = "__AETHEUS_CANCEL_REQUESTED";
    // Who cancelled the run and why, kept beside the request marker for the run page.
    internal const string CancelledByVariable = "__AETHEUS_CANCELLED_BY";
    internal const string CancellationReasonVariable = "__AETHEUS_CANCEL_REASON";

    public const int MaxMatrixAxes = 8;
    public const int MaxMatrixValuesPerAxis = 32;
    public const int MaxMatrixLegs = 256;

    // --- Output-variable regex ---

    [GeneratedRegex(@"##aetheus\[setvariable name=(\w+)\](.+)$", RegexOptions.Multiline)]
    private static partial Regex OutputVariablePattern();

    // --- Variable substitution ($(VarName) syntax) ---

    public static string SubstituteVariables(string command, Dictionary<string, string> variables)
        => PipelineCommandBuilder.Substitute(command, variables);

    /// <summary>Returns a copy of <paramref name="vars"/> with all keys flagged as secrets removed.</summary>
    public static Dictionary<string, string> FilterSecretKeys(Dictionary<string, string> vars, HashSet<string> secretKeys)
    {
        if (secretKeys.Count == 0) return vars;
        var filtered = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in vars)
        {
            if (!secretKeys.Contains(k)) filtered[k] = v;
        }
        return filtered;
    }

    /// <summary>Returns a copy of <paramref name="vars"/> with all secret keys' values replaced by <c>***</c>.</summary>
    public static Dictionary<string, string> MaskSecretValues(Dictionary<string, string> vars, HashSet<string> secretKeys)
    {
        if (secretKeys.Count == 0) return vars;
        var masked = new Dictionary<string, string>(vars, StringComparer.OrdinalIgnoreCase);
        foreach (var key in secretKeys)
        {
            if (masked.ContainsKey(key)) masked[key] = "***";
        }
        return masked;
    }

    // Run-list readers delegate to the Monitoring read model.
    public static Dictionary<string, string> DeserializeResolvedVariables(string? json) =>
        PipelineRunListRows.DeserializeResolvedVariables(json);

    public static List<string> DeserializeWarnings(string? json) => PipelineRunListRows.DeserializeWarnings(json);

    public static bool HasCancellationRequest(string? additionalVariablesJson) =>
        DeserializeResolvedVariables(additionalVariablesJson)
            .TryGetValue(PipelineRunHelpers.CancellationRequestedVariable, out var requested)
        && string.Equals(requested, "true", StringComparison.OrdinalIgnoreCase);

    public static List<CoverageFileDto> DeserializeCoverageFiles(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return [];

        try { return JsonSerializer.Deserialize<List<CoverageFileDto>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    // --- DTO mapping: PipelineRunDtoMapper (RunListProjection, MapRunToDto, HydrateListWarnings) ---

    // --- Fail-closed isolation enforcement ---

    public static string? CheckIsolationPolicy(string stageName, PipelineStageDefinition stageDef, Server server)
    {
        var wantsContainer = stageDef.Isolation?.IsContainer == true;

        var contractError = PipelineIsolationHelpers.ValidateDefinition(
            $"Stage '{stageName}'", stageDef.Isolation);
        if (contractError is not null)
            return contractError;

        if (wantsContainer && !server.DockerAvailable)
            return $"Stage '{stageName}' requires container isolation but runner '{server.Name}' has no Docker available. " +
                   "Install Docker on the runner or remove `isolation: container` from the stage.";

        if (server.RequireContainerIsolation && !wantsContainer)
            return $"Stage '{stageName}' must run with container isolation: runner '{server.Name}' enforces a containers-only policy. " +
                   "Add `isolation: { mode: container, image: ... }` to the stage.";

        return null;
    }

    public static string? CheckRunIsolationPolicy(Server server, PipelineIsolationDefinition? runIsolation)
    {
        var wantsContainer = runIsolation?.IsContainer == true;

        var contractError = PipelineIsolationHelpers.ValidateDefinition("Run", runIsolation);
        if (contractError is not null)
            return contractError;
        if (wantsContainer && !string.IsNullOrWhiteSpace(runIsolation?.Toolchain))
            return "Run-level isolation cannot resolve a repository toolchain before checkout. " +
                   "Declare the toolchain per stage or job.";

        if (wantsContainer && !server.DockerAvailable)
            return $"Run requires container isolation but runner '{server.Name}' has no Docker available. " +
                   "Install Docker on the runner or remove run-level `isolation: container`.";

        if (server.RequireContainerIsolation && !wantsContainer)
            return $"Runner '{server.Name}' enforces a containers-only policy but the run is not container-isolated. " +
                   "Add run-level `isolation: { mode: container, image: ... }`.";

        return null;
    }

    // --- P-10: Condition Evaluation ---

    /// <summary>Evaluates a stage or step condition (<see cref="PipelineConditionEvaluator"/>). An
    /// unsupported condition comes back invalid, never as met or not met: the caller fails the stage or
    /// step with the verdict's error.</summary>
    public static PipelineConditionVerdict EvaluateCondition(string? condition, Dictionary<string, string> variables, bool previousStageFailed)
        => PipelineConditionEvaluator.Evaluate(condition, variables, previousStageFailed);

    // --- P-12: Output Variables ---

    public static Dictionary<string, string> ParseOutputVariablesFromLogs(string? output) => ParseOutputVariables(output);

    private static Dictionary<string, string> ParseOutputVariables(string? output)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(output)) return result;

        var matches = OutputVariablePattern().Matches(output);
        foreach (Match match in matches)
        {
            result[match.Groups[1].Value] = match.Groups[2].Value.Trim();
        }

        return result;
    }

    // S-TECH-56: true when dispatching this stage's job would push its group over MaxParallel.
    public static bool IsGroupThrottled(
        PipelineStageDefinition stageDef, List<PipelineStageDefinition> flattenedStages,
        List<string> activeStageNames, Dictionary<string, int> dispatchedPerGroup)
    {
        if (stageDef.Strategy is not { } strategy || string.IsNullOrEmpty(stageDef.Group))
            return false;

        var maxParallel = Math.Max(1, strategy.MaxParallel);
        var groupJobNames = flattenedStages
            .Where(s => string.Equals(s.Group, stageDef.Group, StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var inFlight = activeStageNames.Count(groupJobNames.Contains);
        var dispatched = dispatchedPerGroup.GetValueOrDefault(stageDef.Group);
        return inFlight + dispatched >= maxParallel;
    }

    // --- P-24: Matrix Expansion ---

    // S2 (audit 2026-07-06): matrix values become $(axis)-substitutable text in step shell scripts
    // (PipelineCommandBuilder.Substitute splices values verbatim - the same channel as WEBHOOK_REF),
    // so constrain them to their legitimate shape at run creation. Same discipline as
    // PipelineWebhookService: \A...\z true-end anchoring (no trailing-newline bypass) and a first
    // char excluding '-' (argument-injection token). No space either - matching the webhook-ref
    // discipline, a space is the shell's argument separator so an unquoted $(axis) splice would
    // word-split. Matrix axes name OS/framework/config values ("ubuntu-22.04", "net8.0",
    // "linux/amd64", "node:20") - they never need a space or a shell metacharacter.
    // F-ENG-02/03: the tail also allows '=' (key=value), '%' (50%), ',', '^' and '~' (semver ranges) - all
    // shell-inert in a bare word. STILL rejected, by design: a leading '-' (argument-injection token), '\'
    // (shell escape, so Windows-style paths must use '/'), spaces and every shell metacharacter.
    [GeneratedRegex(@"\A[A-Za-z0-9._+][A-Za-z0-9._+/:@=%,~^-]{0,1023}\z")]
    private static partial Regex SafeMatrixValueRegex();

    [GeneratedRegex(@"\A[A-Za-z_][A-Za-z0-9_]{0,63}\z")]
    private static partial Regex SafeMatrixAxisRegex();

    /// <summary>Rejects matrix values whose shape could break out of the shell template once
    /// spliced by <c>Substitute</c>. Returns one error per offending value (empty = valid).</summary>
    public static List<string> ValidateMatrixValues(IEnumerable<PipelineStageDefinition> stages)
    {
        var errors = new List<string>();
        foreach (var stage in stages)
        {
            if (stage.Matrix is not { Count: > 0 }) continue;
            if (stage.Matrix.Count > MaxMatrixAxes)
                errors.Add($"Stage '{stage.Name}': matrix has {stage.Matrix.Count} axes; the maximum is {MaxMatrixAxes}.");

            long legCount = 1;
            foreach (var (axis, values) in stage.Matrix)
            {
                if (!SafeMatrixAxisRegex().IsMatch(axis))
                    errors.Add(
                        $"Stage '{stage.Name}': matrix axis '{axis}' must be a safe variable name (letter or underscore first, then letters, digits or underscores; maximum 64 characters).");
                if (values is null || values.Count == 0)
                {
                    errors.Add($"Stage '{stage.Name}': matrix axis '{axis}' must contain at least one value.");
                    legCount = 0;
                    continue;
                }
                if (values.Count > MaxMatrixValuesPerAxis)
                    errors.Add($"Stage '{stage.Name}': matrix axis '{axis}' has {values.Count} values; the maximum is {MaxMatrixValuesPerAxis}.");
                legCount = Math.Min((long)MaxMatrixLegs + 1, legCount * values.Count);
                foreach (var value in values)
                {
                    if (value is null || !SafeMatrixValueRegex().IsMatch(value))
                        errors.Add($"Stage '{stage.Name}': matrix value '{value}' for axis '{axis}' contains characters outside the allowed shape (letters, digits, '._+/:@=%,~^-', no leading '-', no backslash or space).");
                }
            }
            if (legCount > MaxMatrixLegs)
                errors.Add($"Stage '{stage.Name}': matrix expands beyond the maximum of {MaxMatrixLegs} legs.");
        }
        return errors;
    }

    public static List<string> ValidateExecutionRoles(IEnumerable<PipelineStageDefinition> stages)
    {
        var errors = new List<string>();
        foreach (var stage in stages)
        {
            if (string.IsNullOrWhiteSpace(stage.ExecutionRole)) continue;
            if (!stage.ExecutionRole.Equals("build", StringComparison.OrdinalIgnoreCase)
                && !stage.ExecutionRole.Equals("deploy", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(
                    $"Stage '{stage.Name}': execution_role must be 'build' or 'deploy', not '{stage.ExecutionRole}'.");
            }
        }
        return errors;
    }

    public static List<Dictionary<string, string>> ExpandMatrix(Dictionary<string, List<string>>? matrix)
    {
        if (matrix is null || matrix.Count == 0)
            return [new Dictionary<string, string>()]; // Single empty leg for non-matrix stages

        var cardinalityErrors = ValidateMatrixValues(
            [new PipelineStageDefinition { Name = "matrix", Matrix = matrix }]);
        if (cardinalityErrors.Count > 0)
            throw new ArgumentException(string.Join(" ", cardinalityErrors), nameof(matrix));

        var keys = matrix.Keys.ToList();
        var result = new List<Dictionary<string, string>> { new() };

        foreach (var key in keys)
        {
            var values = matrix[key];
            var expanded = new List<Dictionary<string, string>>();
            foreach (var existing in result)
            {
                foreach (var value in values)
                {
                    var newLeg = new Dictionary<string, string>(existing) { [key] = value };
                    expanded.Add(newLeg);
                }
            }
            result = expanded;
        }

        return result;
    }

    public static List<string> BuildDeadlockReasons(
        List<PipelineStepRun> pendingSteps, PipelineYamlDefinition definition, List<string> completedStages)
    {
        var reasons = new List<string>();
        foreach (var stageName in pendingSteps.Select(s => s.StageName).Distinct())
        {
            if (stageName is PipelineRunHelpers.SystemPrepareStage or PipelineRunHelpers.SystemCleanupStage) continue;
            var stageDef = YamlParsingHelper.FlattenJobs(definition).FirstOrDefault(s => s.Name == stageName);
            if (stageDef is null)
            {
                reasons.Add($"Stage '{stageName}' is not defined in the pipeline and cannot run.");
                continue;
            }

            var unmet = stageDef.DependsOn.Where(d => !completedStages.Contains(d)).ToList();
            if (unmet.Count > 0)
                reasons.Add(
                    $"Stage '{stageName}' is blocked: it depends on " +
                    $"{string.Join(", ", unmet.Select(u => $"'{u}'"))} which did not complete successfully.");
        }

        if (reasons.Count == 0)
            reasons.Add("Pipeline is deadlocked: pending stages cannot be scheduled.");

        return reasons;
    }

    public static (PreflightTargetKind Kind, string Label) DescribeTarget(PipelineStageDefinition stage)
    {
        if (!string.IsNullOrEmpty(stage.Pool))
            return (PreflightTargetKind.Pool, stage.Pool);
        if (!string.IsNullOrEmpty(stage.Environment))
            return (PreflightTargetKind.Environment, stage.Environment);
        // A stage naming no agent has no label: the front says "any online agent" in the user's language.
        return (PreflightTargetKind.Agent, stage.Agent ?? string.Empty);
    }

    // --- Step env scoping / dispatch / container isolation (from the task-creation path) ---

    public static Dictionary<string, string> ScopeStepEnvironment(
        Dictionary<string, string> legVars, HashSet<string> secretKeys, PipelineStepDefinition stepDef)
    {
        // S-TECH-V6QN: drop dotted keys (e.g. the "parameters.X" namespace) from the exported env.
        if (legVars.Keys.Any(k => k.Contains('.', StringComparison.Ordinal)))
            legVars = legVars.Where(kv => !kv.Key.Contains('.', StringComparison.Ordinal))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

        if (secretKeys.Count == 0) return legVars;
        var referenceText = stepDef.Shell + "\n" + (stepDef.WorkingDirectory ?? string.Empty);
        if (string.Equals(stepDef.Type, "publish-observability", StringComparison.OrdinalIgnoreCase))
            referenceText += "\nAETHEUS_NUGET_SIGNING_PFX_BASE64\nAETHEUS_NUGET_SIGNING_PFX_PASSWORD"
                + "\nAETHEUS_NUGET_SIGNING_CERTIFICATE_FINGERPRINT"
                + "\nAETHEUS_PACKAGE_BASE_URL\nAETHEUS_PACKAGE_TOKEN";
        // A `checkout: true` step runs the auto-injected git-clone preamble, which references the
        // git credential vars even though the user's shell text never mentions them.
        if (stepDef.Checkout)
            referenceText += "\nGIT_USERNAME\nGIT_PASSWORD";
        var scoped = new Dictionary<string, string>(legVars, StringComparer.OrdinalIgnoreCase);
        foreach (var key in secretKeys)
        {
            if (scoped.ContainsKey(key) && !referenceText.Contains(key, StringComparison.OrdinalIgnoreCase))
                scoped.Remove(key);
        }
        return scoped;
    }

    public static void MarkStepDispatched(PipelineStepRun stepRun, ServerTask task)
    {
        stepRun.Task = task;
        stepRun.Status = TaskExecutionStatus.Assigned;
    }

    public static IReadOnlyList<string> ApplyContainerIsolation(
        ServerTask task,
        PipelineIsolationDefinition? isolation) =>
        PipelineIsolationHelpers.Apply(task, isolation);

    public static List<string> ValidateIsolationLimits(IEnumerable<PipelineStageDefinition> stages) =>
        PipelineIsolationHelpers.ValidateLimits(stages);

    public static List<string> ValidateIsolationDefinitions(
        PipelineIsolationDefinition? runIsolation,
        IEnumerable<PipelineStageDefinition> stages) =>
        PipelineIsolationHelpers.ValidateDefinitions(runIsolation, stages);

    public static Dictionary<string, string> ResolveLegVariables(
        Dictionary<string, string> stageVars, string legKey, List<Dictionary<string, string>> matrixLegs)
    {
        var legVars = new Dictionary<string, string>(stageVars, StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(legKey)) return legVars;

        var matchingLeg = matrixLegs.FirstOrDefault(l => string.Join("-", l.Values) == legKey);
        if (matchingLeg is not null)
        {
            foreach (var (key, value) in matchingLeg)
                legVars[key] = value;
        }
        return legVars;
    }

    /// <summary>
    /// Marks a step failed without an agent ever having run it - a fail-closed decision taken by the
    /// engine itself. Moved here from the run service when the step factories came out: they all need
    /// it, and a second copy would be a second version of what "failed" means.
    /// </summary>
    public static void MarkSystemStepFailed(
        PipelineStepRun stepRun,
        string failureCode,
        string failureReason,
        DateTime failedAt)
    {
        stepRun.Status = TaskExecutionStatus.Failed;
        stepRun.ExitCode ??= -1;
        stepRun.StartedAt ??= failedAt;
        stepRun.CompletedAt = failedAt;
        stepRun.FailureCode = failureCode;
        stepRun.FailureReason = failureReason.Length <= 2048
            ? failureReason
            : failureReason[..2048];
    }

    /// <summary>True for a full SHA-1 or SHA-256 commit hash - the immutability check the deploy path
    /// relies on to refuse anything but a pinned commit.</summary>
    internal static bool IsGitCommitHash(string? value)
        => value is { Length: 40 or 64 } && value.All(Uri.IsHexDigit);
}
