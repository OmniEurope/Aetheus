// SPDX-License-Identifier: EUPL-1.2
using static Aetheus.Back.Components.Pipelines.PipelineRunHelpers;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>Expands merged variables during resolution while preserving references to values not
/// yet available. Each dispatch incorporates step outputs and re-expands declared values, delaying
/// defaults for runner-scoped names until runner selection.</summary>
internal static class PipelineVariableExpansion
{
    /// <summary>Enough for a chain of three references, the depth resolution always allowed.</summary>
    private const int MaxPasses = 3;

    /// <summary>
    /// Injected per stage leg once a runner is picked, after this expansion
    /// (<see cref="PipelineVariableResolver.InjectStageSystemVariables"/>). A default on one of them
    /// is left pending rather than applied in place of the runner's real value.
    /// </summary>
    private static readonly HashSet<string> InjectedPerLeg = new(StringComparer.OrdinalIgnoreCase)
    {
        "SYSTEM_STAGENAME", "AGENT_NAME", "AGENT_HOSTNAME", "AGENT_OS", "AGENT_PLATFORM", "AGENT_ID"
    };

    private static readonly IReadOnlySet<string> NoHiddenNames = new HashSet<string>();

    /// <summary>
    /// Resolution-time expansion. A vault secret is expanded exactly as before the default syntax
    /// existed (its own <c>$(NAME)</c> references resolve, a <c>$(NAME:-x)</c> shape stays as typed):
    /// it is opaque data, not a template.
    /// <para>
    /// BACK-022: a variable that references a secret, directly or through another variable
    /// (<c>ALIAS: $(VAULT_SECRET)</c>), receives its clear value here. It joins
    /// <paramref name="secretKeys"/>, so it is classified like the secret everywhere that set is read:
    /// left out of the persisted run variables, kept out of the environment of a step that does not
    /// reference it, and passed to the command as a placeholder rather than as text.
    /// </para>
    /// </summary>
    internal static void ExpandResolved(
        Dictionary<string, string> resolved,
        ISet<string> secretKeys,
        IReadOnlySet<string> namesStillToCome)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentNullException.ThrowIfNull(secretKeys);
        var vaultSecrets = new HashSet<string>(secretKeys, StringComparer.OrdinalIgnoreCase);
        var declared = new Dictionary<string, string>(resolved, StringComparer.OrdinalIgnoreCase);
        Func<string, bool> applies = name => !namesStillToCome.Contains(name);
        Expand(resolved, _ => true, key => vaultSecrets.Contains(key) ? null : applies, NoHiddenNames);
        ClassifySecretAliases(declared, secretKeys);
    }

    // Read on the values as declared, before expansion replaced the references: the taint follows the
    // reference graph to its fixed point, so a chain longer than the expansion depth is still covered.
    private static void ClassifySecretAliases(IReadOnlyDictionary<string, string> declared, ISet<string> secretKeys)
    {
        bool added;
        do
        {
            added = false;
            foreach (var (key, value) in declared)
            {
                if (secretKeys.Contains(key)) continue;
                if (!PipelineCommandBuilder.ReferencedVariableNames(value).Any(secretKeys.Contains)) continue;
                secretKeys.Add(key);
                added = true;
            }
        }
        while (added);
    }

    /// <summary>
    /// Injects the outputs of the run's successful steps, then expands the run variables again so a
    /// declared value can reference them. An output overrides a declared value of the same name,
    /// except <see cref="PipelineDeploymentTargetGuard.TargetVariable"/>, which only the definition
    /// may set. What is re-expanded is deliberately narrow: never a secret (opaque data), never an
    /// output (data a step produced, not a template), never the deployment target, and a secret's
    /// value is never substituted into another variable - that would carry it past the per-step
    /// secret scoping into a variable every step receives.
    /// </summary>
    internal static void ApplyStepOutputs(
        Dictionary<string, string> variables,
        IReadOnlySet<string> secretKeys,
        IEnumerable<StepOutputProjection> stepOutputs)
    {
        ArgumentNullException.ThrowIfNull(variables);
        var injected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in stepOutputs)
        {
            foreach (var (key, value) in DeserializeResolvedVariables(step.OutputVariablesJson))
            {
                var qualified = $"{step.StageName}.{step.StepName}.{key}";
                variables[qualified] = value;
                injected.Add(qualified);
                if (key.Equals(PipelineDeploymentTargetGuard.TargetVariable, StringComparison.OrdinalIgnoreCase))
                    continue;
                variables[key] = value;
                injected.Add(key);
            }
        }

        Func<string, bool> applies = name => !InjectedPerLeg.Contains(name);
        Expand(
            variables,
            key => !secretKeys.Contains(key)
                && !injected.Contains(key)
                && !key.Equals(PipelineDeploymentTargetGuard.TargetVariable, StringComparison.OrdinalIgnoreCase),
            _ => applies,
            secretKeys);
    }

    private static void Expand(
        Dictionary<string, string> variables,
        Func<string, bool> expands,
        Func<string, Func<string, bool>?> defaultsFor,
        IReadOnlySet<string> hidden)
    {
        for (var pass = 0; pass < MaxPasses; pass++)
        {
            IReadOnlyDictionary<string, string> lookup = hidden.Count == 0
                ? variables
                : variables.Where(entry => !hidden.Contains(entry.Key))
                    .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
            var anyReplaced = false;
            foreach (var key in variables.Keys.ToList())
            {
                var value = variables[key];
                if (!expands(key) || !value.Contains("$(", StringComparison.Ordinal)) continue;
                var expanded = PipelineCommandBuilder.Substitute(value, lookup, defaultsFor(key));
                if (expanded == value) continue;
                variables[key] = expanded;
                anyReplaced = true;
            }
            if (!anyReplaced) break;
        }
    }
}
