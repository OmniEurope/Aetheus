// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Pipelines;

/// <summary>
/// The values of the <c>deployed_baseline</c> key of a <c>type: restore-artifacts</c> step, and the
/// environment variable that carries the opt-out from the control plane to the agent.
///
/// Shared because both sides must spell them identically: a name that drifted on one side would not
/// fail to compile, it would silently turn the opt-out back into the fail-closed default (or worse,
/// the reverse).
/// </summary>
public static class PipelineDeployedBaseline
{
    /// <summary>Default: a deployed-release restore reads the archive's delivery contract and exports
    /// it, and fails when the archive carries none.</summary>
    public const string Required = "required";

    /// <summary>Explicit opt-out for an artifact that legitimately ships without a delivery contract.</summary>
    public const string None = "none";

    /// <summary>Set to <see cref="None"/> only when the step opted out; absent otherwise.</summary>
    public const string EnvironmentVariable = "AETHEUS_RESTORE_DEPLOYED_BASELINE";

    /// <summary>Whether the raw YAML value is one of the two accepted values (null or blank means the
    /// default and is accepted).</summary>
    public static bool IsValid(string? value) =>
        string.IsNullOrWhiteSpace(value)
        || string.Equals(value.Trim(), Required, StringComparison.OrdinalIgnoreCase)
        || string.Equals(value.Trim(), None, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the raw value is the explicit opt-out.</summary>
    public static bool IsOptedOut(string? value) =>
        string.Equals(value?.Trim(), None, StringComparison.OrdinalIgnoreCase);
}
