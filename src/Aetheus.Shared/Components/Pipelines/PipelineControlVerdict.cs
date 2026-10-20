// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Pipelines;

/// <summary>
/// Recette R1-80 (after R2-146): a control that ran to the end and found failures is Rejected, not a
/// success with a small warning, and not a technical error either. The step that ran the tests did its
/// work (its command succeeded), so its execution status stays Success; what it found is read from the
/// outputs it published:
/// <list type="bullet">
/// <item>a required test gate of the candidate's seal set to a non-zero value (its suite found failures),</item>
/// <item>or the seal itself refusing the result (<c>CANDIDATE_DEPLOYABLE=false</c>).</item>
/// </list>
/// The gate list is the seal's own (deploy/scripts/generate-candidate-assurance-contract.mjs, its
/// <c>tests</c> object): every one of them grades F when non-zero. An advisory probe such as
/// <c>SMOKE_GATE_STATUS</c> (a <c>type: smoke</c> step with the default <c>evidence_gate: advisory</c>)
/// is deliberately not in it: a finding it records does not refuse anything.
/// A trigger step carries the outputs of the run it started, so a rejected child rejects its parent.
/// </summary>
public static class PipelineControlVerdict
{
    /// <summary>The output a candidate's assurance seal publishes; "false" refuses the result (R2-146).</summary>
    public const string DeployableVerdictVariable = "CANDIDATE_DEPLOYABLE";

    /// <summary>The seal's test keys and the status variable each one is graded from.</summary>
    public static readonly IReadOnlyDictionary<string, string> TestGateStatusVariables =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
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

    private static readonly HashSet<string> GateVariables =
        new(TestGateStatusVariables.Values, StringComparer.OrdinalIgnoreCase);

    /// <summary>A status that is set and is not 0: the suite found something.</summary>
    public static bool IsFindings(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim() != "0";

    /// <summary>True when one of these outputs refuses the result: a required test gate with findings,
    /// or the seal's explicit refusal.</summary>
    public static bool RejectsResult(IEnumerable<KeyValuePair<string, string>> outputs)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        return outputs.Any(variable =>
            (GateVariables.Contains(variable.Key) && IsFindings(variable.Value))
            || (string.Equals(variable.Key, DeployableVerdictVariable, StringComparison.OrdinalIgnoreCase)
                && bool.TryParse(variable.Value, out var deployable) && !deployable));
    }
}
