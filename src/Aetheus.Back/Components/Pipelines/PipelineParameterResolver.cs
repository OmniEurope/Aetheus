// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// P (queue-time run parameters): validates user-supplied parameter values against a pipeline's
/// declared <c>parameters:</c> block and computes the effective value (supplied value, else the
/// declared default) for each parameter. Type/required/allowed-values are enforced here so an
/// invalid launch is rejected before a run is created.
/// </summary>
public static class PipelineParameterResolver
{
    /// <summary>
    /// Validates <paramref name="supplied"/> against <paramref name="declared"/>. On success returns
    /// the effective name→value map (only declared parameters that resolve to a non-null value).
    /// </summary>
    /// <param name="enforceRequired">False for a preview that runs before the user has answered: a
    /// required parameter with no value is then not an error (the launch itself enforces it), so the
    /// preview does not warn about a field the user has not been asked to fill yet.</param>
    public static bool TryResolve(
        IReadOnlyList<PipelineTemplateParameterDefinition> declared,
        IReadOnlyDictionary<string, string>? supplied,
        out Dictionary<string, string> effective,
        out List<string> errors,
        bool enforceRequired = true)
    {
        effective = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        errors = [];
        supplied ??= new Dictionary<string, string>();

        var declaredNames = declared
            .Select(p => p.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Reject inputs that don't correspond to a declared parameter (typo / stale dialog).
        foreach (var key in supplied.Keys)
            if (!declaredNames.Contains(key))
                errors.Add($"Unknown parameter '{key}'.");
        foreach (var param in declared)
            ResolveParameter(param, supplied, effective, errors, enforceRequired);
        return errors.Count == 0;
    }

    private static void ResolveParameter(
        PipelineTemplateParameterDefinition parameter,
        IReadOnlyDictionary<string, string> supplied,
        IDictionary<string, string> effective,
        ICollection<string> errors,
        bool enforceRequired)
    {
        if (string.IsNullOrWhiteSpace(parameter.Name)) return;
        if (IsReservedName(parameter.Name))
        {
            errors.Add($"Parameter '{parameter.Name}' uses a reserved system variable name.");
            return;
        }
        var hasSupplied = supplied.TryGetValue(parameter.Name, out var rawValue)
            && !string.IsNullOrWhiteSpace(rawValue);
        var value = hasSupplied ? rawValue! : parameter.Default;
        if (string.IsNullOrWhiteSpace(value))
        {
            if (parameter.Required && enforceRequired) errors.Add($"Parameter '{parameter.Name}' is required.");
            return;
        }
        ValidateParameterType(parameter, value, errors);
        effective[parameter.Name] = value;
    }

    private static void ValidateParameterType(
        PipelineTemplateParameterDefinition parameter,
        string value,
        ICollection<string> errors)
    {
        var type = (parameter.Type ?? "string").Trim().ToLowerInvariant();
        if (type == "number"
            && !decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
            errors.Add($"Parameter '{parameter.Name}' must be a number (got '{value}').");
        else if (type == "boolean" && !bool.TryParse(value, out _))
            errors.Add($"Parameter '{parameter.Name}' must be 'true' or 'false' (got '{value}').");
        else if (type == "choice" && parameter.AllowedValues.Count > 0
                 && !parameter.AllowedValues.Any(item => string.Equals(item, value, StringComparison.Ordinal)))
            errors.Add($"Parameter '{parameter.Name}' must be one of: {string.Join(", ", parameter.AllowedValues)} (got '{value}').");
    }

    /// <summary>
    /// Validates the <c>parameters:</c> block itself, with no queue-time values in hand: reserved
    /// names, and defaults that contradict their own declared type.
    ///
    /// This exists because saving a pipeline and launching one ask different questions.
    /// <see cref="TryResolve"/> answers "can this run start with these values", and a required
    /// parameter with no value is rightly fatal there. Saving asks only "is this definition
    /// well-formed", and running the launch check against an empty value set made a required
    /// parameter without a default impossible to store at all: CreatePipeline rejected the exact
    /// shape aetheus-deploy-prod ships, which only existed because the repository-sync path skips
    /// the check. Missing values stay a launch-time concern, enforced where the launch happens.
    /// </summary>
    public static List<string> ValidateDeclarations(
        IReadOnlyList<PipelineTemplateParameterDefinition> declared)
    {
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in declared)
        {
            if (string.IsNullOrWhiteSpace(parameter.Name)) continue;
            if (IsReservedName(parameter.Name))
            {
                errors.Add($"Parameter '{parameter.Name}' uses a reserved system variable name.");
                continue;
            }
            if (!seen.Add(parameter.Name))
                errors.Add($"Parameter '{parameter.Name}' is declared more than once.");
            if (!string.IsNullOrWhiteSpace(parameter.Default))
                ValidateParameterType(parameter, parameter.Default!, errors);
        }
        return errors;
    }

    internal static bool IsReservedName(string name)
        => name.StartsWith("AETHEUS_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("BUILD_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("SYSTEM_", StringComparison.OrdinalIgnoreCase)
            // The deployment bootstrap credential opens an Admin JWT on the freshly deployed target.
            // Leaving its names unreserved let a pipeline parameter declare one and carry a chosen
            // administrator password through the run, so the prefix is reserved like the others.
            || name.StartsWith("DEPLOYMENT_BOOTSTRAP_", StringComparison.OrdinalIgnoreCase)
            || name.Equals("BOOTSTRAP_STAMP", StringComparison.OrdinalIgnoreCase)
            // The parent context of a run started by another (UPSTREAM_RUN_ID, UPSTREAM_PIPELINE,
            // UPSTREAM_CHAIN, UPSTREAM_RELEASE) is written by the engine alone: a launch parameter of
            // that name would let a run claim any parent in the run lineage (audit of 2026-09-30).
            || name.StartsWith("UPSTREAM_", StringComparison.OrdinalIgnoreCase)
            // The Gate's verdict on the deployed release (RELEASE_GATE_STATUS, RELEASE_GATE_GRADE,
            // RELEASE_GATE_APPROVAL) is read from the release by the engine alone (recette R-24).
            || name.StartsWith("RELEASE_GATE_", StringComparison.OrdinalIgnoreCase);

    /// <summary>The declared defaults (name→default) for parameters that declare one - injected at the
    /// lowest precedence so an explicit YAML <c>variables:</c> entry of the same name still wins.</summary>
    public static Dictionary<string, string> Defaults(IReadOnlyList<PipelineTemplateParameterDefinition> declared)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in declared)
        {
            if (!string.IsNullOrWhiteSpace(p.Name)
                && !IsReservedName(p.Name)
                && !string.IsNullOrWhiteSpace(p.Default))
                map[p.Name] = p.Default!;
        }
        return map;
    }
}
