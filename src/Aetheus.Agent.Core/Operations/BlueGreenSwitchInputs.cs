// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// What a blue-green switch needs from the step's environment: the revision the journal records, the
/// reload helper that applies the switch, and the upstream template and configuration it renders.
/// Rendering the configuration is the switch: reloading without rewriting it would reload the
/// configuration that is already live, so the step would report success while traffic stayed on the
/// previous colour.
/// </summary>
internal sealed record BlueGreenSwitchInputs(string Revision, string ReloadCommand, string TemplatePath, string ConfPath)
{
    /// <summary>Reads the inputs, or the refusal that names the first missing one, in the order the
    /// switch always checked them.</summary>
    public static BlueGreenSwitchInputs? Read(IReadOnlyDictionary<string, string> envVars, out string refusal)
    {
        var inputs = new BlueGreenSwitchInputs(
            Value(envVars, "AETHEUS_BG_REVISION"),
            Value(envVars, "AETHEUS_BG_RELOAD_HELPER"),
            Value(envVars, "AETHEUS_BG_UPSTREAM_TEMPLATE"),
            Value(envVars, "AETHEUS_BG_UPSTREAM_CONF"));
        refusal = inputs.Refusal() ?? string.Empty;
        return refusal.Length == 0 ? inputs : null;
    }

    private string? Refusal()
    {
        if (Revision.Length == 0)
            return "AETHEUS_BG_REVISION is required so the journal records what was deployed.";
        if (ReloadCommand.Length == 0)
            return "AETHEUS_BG_RELOAD_HELPER is required to apply the switch.";
        if (TemplatePath.Length == 0 || ConfPath.Length == 0)
            return "AETHEUS_BG_UPSTREAM_TEMPLATE and AETHEUS_BG_UPSTREAM_CONF are required: without them the switch would reload the live configuration unchanged.";
        return File.Exists(TemplatePath) ? null : $"Upstream template is missing: {TemplatePath}";
    }

    private static string Value(IReadOnlyDictionary<string, string> envVars, string name) =>
        envVars.GetValueOrDefault(name, string.Empty).Trim();
}
