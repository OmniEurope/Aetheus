// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The <c>tests</c> section of the candidate assurance contract, built as
/// <c>deploy/pipelines/toto-conformance-fixture/common/deploy/scripts/generate-candidate-assurance-contract.mjs</c> built it from the gate-status
/// variables of the run: property for property and in the same order, since the contract is sealed
/// over its JSON text.
///
/// <paramref name="variable"/> reads a variable with <see cref="FunctionShell.Variable"/>'s meaning (an
/// empty value is unset), which every expression of the script here gives the same answer for: an
/// outcome is <c>"0"</c>, a positive integer or unavailable; a compatibility attestation that is empty
/// falls back to its legacy name exactly when it is unset; an empty tested revision is written null.
/// </summary>
internal sealed partial class AssuranceTestOutcomes(Func<string, string?> variable, string profile, ISet<string> inapplicable)
{
    // The persist-compatibility-mode function publishes COMPATIBILITY_MODE and PREVIOUS_VERSION_TESTED;
    // the script it replaced published them as CompatibilityMode and PreviousVersionTested, which runs
    // stored before the move still carry. The upper snake case name wins; the legacy one is read only
    // when it is absent or empty.
    private static readonly Dictionary<string, string> LegacyCompatibilityVariables = new(StringComparer.Ordinal)
    {
        ["COMPATIBILITY_MODE"] = "CompatibilityMode",
        ["PREVIOUS_VERSION_TESTED"] = "PreviousVersionTested"
    };

    [GeneratedRegex("^[1-9][0-9]*\\z")]
    private static partial Regex PositiveInteger();

    /// <summary><c>/^[1-9][0-9]*$/.test(value ?? "")</c>.</summary>
    public static bool IsPositiveInteger(string? value) => PositiveInteger().IsMatch(value ?? string.Empty);

    /// <summary><c>Number(value)</c> of a decimal integer: the nearest double.</summary>
    public static double ToNumber(string digits) => double.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);

    /// <summary>The section, in the script's order.</summary>
    public JsObject Build()
    {
        var tests = new JsObject();
        tests.Set("unit", Declared("unit", () => Required(Outcome("UNIT_TEST_GATE_STATUS"))));
        tests.Set("analyzers", Declared("analyzers", () => Required(Outcome("ANALYZER_TEST_GATE_STATUS"))));
        tests.Set("currentIntegration", Declared("currentIntegration", () => WithFlaky(
            Required(Outcome("QA_CURRENT_INTEGRATION_GATE_STATUS")), "QA_CURRENT_INTEGRATION_FLAKY_TESTS")));
        tests.Set("currentE2E", Declared("currentE2E", () => WithFlaky(
            Required(Outcome("QA_CURRENT_E2E_GATE_STATUS")), "QA_CURRENT_E2E_FLAKY_TESTS")));
        tests.Set("previousSmoke", Declared("previousSmoke", () => Required(CompatibilityOutcome(
            "QA_PREVIOUS_SMOKE_GATE_STATUS", "COMPATIBILITY_MODE", "PREVIOUS_VERSION_TESTED"))));
        tests.Set("agentCompatibility", Declared("agentCompatibility", () => Profiled("agentCompatibility", () => Required(CompatibilityOutcome(
            "QA_AGENT_COMPATIBILITY_GATE_STATUS", "AgentCompatibilityMode", "PreviousAgentTested")))));
        tests.Set("previousIntegration", Declared("previousIntegration", () => Profiled("previousIntegration", () => WithFlaky(Required(CompatibilityOutcome(
            "QA_PREVIOUS_INTEGRATION_GATE_STATUS", "COMPATIBILITY_MODE", "PREVIOUS_VERSION_TESTED")), "QA_PREVIOUS_INTEGRATION_FLAKY_TESTS"))));
        tests.Set("previousE2E", Declared("previousE2E", () => Profiled("previousE2E", () => WithFlaky(Required(CompatibilityOutcome(
            "QA_PREVIOUS_E2E_GATE_STATUS", "COMPATIBILITY_MODE", "PREVIOUS_VERSION_TESTED")), "QA_PREVIOUS_E2E_FLAKY_TESTS"))));
        tests.Set("performance", Performance());
        return tests;
    }

    /// <summary>The performance smoke: unrequired when explicitly disabled, and Unavailable (its grade
    /// left as the outcome set it) when the setting is neither true nor false.</summary>
    private JsObject Performance()
    {
        var setting = variable("AETHEUS_ASSURANCE_PERFORMANCE_ENABLED");
        var performance = setting == "false"
            ? NotRun(reason: null)
            : Required(Outcome("QA_PERFORMANCE_GATE_STATUS"));
        if (setting is not ("true" or "false"))
        {
            performance.Set("reason", "performance-setting-unavailable");
            performance.Set("status", "Unavailable");
        }
        return performance;
    }

    private JsObject Outcome(string variableName)
    {
        var value = variable(variableName);
        var outcome = new JsObject();
        if (value == "0")
        {
            outcome.Set("status", "Passed");
            outcome.Set("effectiveGrade", "A");
        }
        else if (IsPositiveInteger(value))
        {
            outcome.Set("status", "Failed");
            outcome.Set("effectiveGrade", "F");
            outcome.Set("exitCode", ToNumber(value!));
        }
        else
        {
            outcome.Set("status", "Unavailable");
            outcome.Set("effectiveGrade", "F");
            outcome.Set("reason", "output-variable-unavailable");
        }
        return outcome;
    }

    private string? CompatibilityVariable(string name)
    {
        var value = variable(name);
        return value is null && LegacyCompatibilityVariables.TryGetValue(name, out var legacy) ? variable(legacy) : value;
    }

    /// <summary><c>{ ...outcome, mode, tested }</c>, or Unavailable when the mode is neither
    /// NMinusOne nor Bootstrap; the spread keeps the outcome's own properties where they were.</summary>
    private JsObject CompatibilityOutcome(string variableName, string modeVariable, string testedVariable)
    {
        var result = Outcome(variableName).Clone();
        var mode = CompatibilityVariable(modeVariable);
        var tested = CompatibilityVariable(testedVariable);
        if (mode is not ("NMinusOne" or "Bootstrap"))
        {
            result.Set("status", "Unavailable");
            result.Set("effectiveGrade", "F");
            result.Set("reason", "compatibility-mode-unavailable");
            result.Set("mode", null);
            result.Set("tested", null);
            return result;
        }
        result.Set("mode", mode);
        result.Set("tested", tested);
        return result;
    }

    /// <summary>The suite's flaky tests, the ones that failed on one attempt and passed on its rerun, sealed
    /// by name as <c>flakyTests</c> (the one-line list the QA pipeline published), after the outcome's own
    /// properties: they do not change the grade, and they are never hidden from it. Set only when the
    /// variable is, so a run without flaky tests seals the very contract it sealed before the rerun
    /// existed; the verifier reads no such property, so a contract with or without it verifies alike.
    /// A difference from the script, which had no rerun to report.</summary>
    private JsObject WithFlaky(JsObject test, string variableName)
    {
        if (variable(variableName) is { } flaky)
            test.Set("flakyTests", flaky);
        return test;
    }

    // Gates an adapting project genuinely cannot produce are recorded as an unrequired NotRun instead
    // of pretending they passed.
    private JsObject Declared(string name, Func<JsObject> build) =>
        inapplicable.Contains(name) ? NotRun("not-applicable-to-project") : build();

    // A light qualification leaves only the retained-agent contract to the nightly.
    private JsObject Profiled(string name, Func<JsObject> build) =>
        profile == "light" && name == "agentCompatibility" ? NotRun("nightly-profile") : build();

    /// <summary><c>{ required: true, ...outcome }</c>.</summary>
    private static JsObject Required(JsObject outcome)
    {
        var test = new JsObject();
        test.Set("required", true);
        foreach (var (key, value) in outcome.Properties)
            test.Set(key, value);
        return test;
    }

    private static JsObject NotRun(string? reason)
    {
        var test = new JsObject();
        test.Set("required", false);
        test.Set("status", "NotRun");
        test.Set("effectiveGrade", null);
        if (reason is not null)
            test.Set("reason", reason);
        return test;
    }
}
