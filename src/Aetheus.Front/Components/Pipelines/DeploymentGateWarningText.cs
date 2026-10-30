// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>R-72: one reason the quality gate has against a release, in the user's language. Shared by the
/// confirmation of a deployment despite the gate and the run page that records it.</summary>
internal static class DeploymentGateWarningText
{
    public static string Describe(DeploymentGateWarningDto warning, IStringLocalizer<AppStrings> localizer)
    {
        ArgumentNullException.ThrowIfNull(warning);
        ArgumentNullException.ThrowIfNull(localizer);
        return warning.Kind switch
        {
            DeploymentGateWarningKind.BelowMinimum =>
                string.Format(localizer["GateWarningBelowMinimum"].Value, warning.Grade, warning.MinimumGrade),
            DeploymentGateWarningKind.BlockingTests =>
                string.Format(localizer["GateWarningBlockingTests"].Value, warning.Count ?? 0),
            DeploymentGateWarningKind.SecurityFindings =>
                string.Format(localizer["GateWarningSecurityFindings"].Value, warning.Count ?? 0),
            _ => localizer[$"GateWarning{warning.Kind}"].Value
        };
    }
}
