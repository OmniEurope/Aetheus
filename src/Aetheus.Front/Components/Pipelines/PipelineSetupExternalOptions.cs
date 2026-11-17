// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// The option names of what the capabilities step offers an external repository (recette R-12): the names of the wizard's address.
/// Kept apart from <see cref="PipelineSetupExternalChoices"/>, which renders the wizard step, so that the architecture rules
/// (which capability each type proposes) read the names without depending on the step.
/// </summary>
internal static class PipelineSetupExternalOptions
{
    public const string Security = "external-security";
    public const string SecurityComplete = "external-security-complete";
    public const string Quality = "external-quality";
    public const string QualityMinimal = "external-quality-minimal";
}
