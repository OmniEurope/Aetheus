// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Pipelines;

/// <summary>Names delivery templates whose roles the platform recognizes through extends
/// references. Role identification does not depend on a project-specific pipeline name.</summary>
public static class DeliveryTemplateNames
{
    public const string Candidate = "application-candidate";
    public const string ReleaseFast = "application-release-fast";
    public const string Ci = "application-ci";
    public const string Nightly = "application-nightly";
    public const string Qa = "application-qa";
    public const string Quality = "application-quality";
    public const string Security = "application-security";
    public const string SecurityExternal = "application-security-external";

    /// <summary>Case-insensitive, as template names are matched everywhere else.</summary>
    public static bool Is(string? templateReferenceName, string templateName) =>
        string.Equals(templateReferenceName?.Trim(), templateName, StringComparison.OrdinalIgnoreCase);
}
