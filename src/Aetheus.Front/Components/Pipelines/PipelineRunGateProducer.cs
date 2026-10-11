// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// Where a gate status came from. The seal says "currentIntegration=Failed", and that test is the
/// status variable QA_CURRENT_INTEGRATION_GATE_STATUS some step published as non-zero, usually two
/// runs down (candidate 2634, its QA child 2638, the step « Test V integration »). The F on the
/// candidate's Gate tab said nothing of that: the step was green and the names of the four failed
/// tests were in its 5253-line log.
///
/// The producer is the FIRST step, in execution order, that set the variable to a non-zero value.
/// A trigger step carries the outputs of the run it started (the control plane copies them up), so
/// when the first setter is a trigger step the search continues in that child: the step that
/// actually measured it is the one to open, not the step that waited for it.
/// </summary>
public static class PipelineRunGateProducer
{
    private const string StatusSuffix = "_GATE_STATUS";
    private const string FailedTestsSuffix = "_FAILED_TESTS";

    /// <summary>The seal's test keys and the status each one is graded from
    /// (deploy/scripts/generate-candidate-assurance-contract.mjs, its <c>tests</c> object).</summary>
    private static readonly Dictionary<string, string> StatusVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        ["unit"] = "UNIT_TEST_GATE_STATUS",
        ["analyzers"] = "ANALYZER_TEST_GATE_STATUS",
        ["currentIntegration"] = "QA_CURRENT_INTEGRATION_GATE_STATUS",
        ["currentE2E"] = "QA_CURRENT_E2E_GATE_STATUS",
        ["previousSmoke"] = "QA_PREVIOUS_SMOKE_GATE_STATUS",
        ["previousIntegration"] = "QA_PREVIOUS_INTEGRATION_GATE_STATUS",
        ["previousE2E"] = "QA_PREVIOUS_E2E_GATE_STATUS",
        ["agentCompatibility"] = "QA_AGENT_COMPATIBILITY_GATE_STATUS",
        ["performance"] = "QA_PERFORMANCE_GATE_STATUS"
    };

    /// <param name="RunId">The run the producing step belongs to.</param>
    /// <param name="PipelineName">That run's pipeline.</param>
    /// <param name="StepId">The step that published the non-zero status.</param>
    /// <param name="StepName">Its name, as the timeline shows it.</param>
    /// <param name="FailedTests">The failed test names the run published beside the status, when it did.</param>
    public sealed record Producer(int RunId, string PipelineName, int StepId, string StepName, IReadOnlyList<string> FailedTests);

    /// <summary>The status variable a seal test key is graded from, null for a key the seal does not declare.</summary>
    public static string? StatusVariableOf(string testKey) =>
        StatusVariables.TryGetValue(testKey, out var variable) ? variable : null;

    /// <summary>QA_CURRENT_INTEGRATION_GATE_STATUS -> QA_CURRENT_INTEGRATION_FAILED_TESTS, as the scripts name it.</summary>
    public static string FailedTestsVariableOf(string statusVariable) =>
        (statusVariable.EndsWith(StatusSuffix, StringComparison.OrdinalIgnoreCase)
            ? statusVariable[..^StatusSuffix.Length]
            : statusVariable) + FailedTestsSuffix;

    /// <summary>A status that is set and is not 0: the suite found something.</summary>
    public static bool IsFindings(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim() != "0";

    /// <summary>A step that succeeded while publishing a non-zero gate status: green, but it holds
    /// findings, and the timeline should not draw it as plainly green.</summary>
    public static bool HasGateFindings(PipelineStepRunDto step) =>
        step.Status == TaskExecutionStatus.Success
        && step.TriggeredRunId is null
        && step.OutputVariables.Any(variable =>
            variable.Key.EndsWith(StatusSuffix, StringComparison.OrdinalIgnoreCase) && IsFindings(variable.Value));

    /// <summary>The step that produced <paramref name="statusVariable"/>, or null when no step of the run
    /// or of its loaded children set it to a non-zero value.</summary>
    public static Producer? Find(
        string statusVariable, PipelineRunDto run, IReadOnlyDictionary<int, PipelineRunDto>? children)
    {
        ArgumentNullException.ThrowIfNull(run);
        children ??= new Dictionary<int, PipelineRunDto>();
        var visited = new HashSet<int>();
        // The trigger chain first; then any loaded child the chain did not reach (a child whose outputs
        // never came back up to its trigger step, because it ended in failure, for instance).
        return FindIn(statusVariable, run, children, visited)
            ?? children.Values.Select(child => FindIn(statusVariable, child, children, visited)).FirstOrDefault(found => found is not null);
    }

    private static Producer? FindIn(
        string statusVariable, PipelineRunDto run, IReadOnlyDictionary<int, PipelineRunDto> children, HashSet<int> visited)
    {
        if (!visited.Add(run.Id)) return null;
        var ordered = ExecutionOrder(run);
        foreach (var step in ordered)
        {
            if (!step.OutputVariables.TryGetValue(statusVariable, out var value) || !IsFindings(value)) continue;
            if (step.TriggeredRunId is { } childId && children.TryGetValue(childId, out var child)
                && FindIn(statusVariable, child, children, visited) is { } nested)
                return nested;
            return new Producer(run.Id, run.PipelineName, step.Id, step.StepName,
                FailedTestsOf(FailedTestsVariableOf(statusVariable), step, ordered));
        }
        return null;
    }

    // The producing step's own list first; otherwise the first step of the same run that published one.
    private static IReadOnlyList<string> FailedTestsOf(
        string variable, PipelineStepRunDto producer, IReadOnlyList<PipelineStepRunDto> runSteps)
    {
        var raw = producer.OutputVariables.GetValueOrDefault(variable)
                  ?? runSteps.Select(step => step.OutputVariables.GetValueOrDefault(variable))
                      .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        return raw is null ? [] : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    // Started first, ran first; a step that never started goes last, and the run's own order breaks ties.
    private static List<PipelineStepRunDto> ExecutionOrder(PipelineRunDto run) =>
        [.. run.Steps
            .Select((step, index) => (Step: step, Index: index))
            .OrderBy(entry => entry.Step.StartedAt ?? DateTime.MaxValue)
            .ThenBy(entry => entry.Index)
            .Select(entry => entry.Step)];
}
