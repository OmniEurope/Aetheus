// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Pipelines;

/// <summary>
/// Recette R2-121: the names of the shipped delivery templates whose role the platform acts on. A
/// pipeline plays a delivery role because it extends one of them (<c>extends: name@N</c>, stored as the
/// pipeline's template reference), whatever the project calls it: the setup wizard writes
/// <c>portfolio-candidate</c>, <c>atlas-ci</c>, and the self-deploy pipelines keep their historical
/// <c>aetheus-*</c> names beside it.
/// </summary>
public static class DeliveryTemplateNames
{
    public const string Candidate = "application-candidate";
    public const string ReleaseFast = "application-release-fast";
    public const string Ci = "application-ci";
    public const string Quality = "application-quality";
    public const string Security = "application-security";

    /// <summary>Case-insensitive, as template names are matched everywhere else.</summary>
    public static bool Is(string? templateReferenceName, string templateName) =>
        string.Equals(templateReferenceName?.Trim(), templateName, StringComparison.OrdinalIgnoreCase);
}
