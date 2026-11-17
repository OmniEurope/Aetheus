// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>What the wizard deduces from the single template it was opened for (<c>?templateId=</c>): which kind of
/// pipeline the template is and, for a package or an external one, the type of repository it stands for.</summary>
internal static class PipelineSetupRequestedTemplate
{
    internal const string ExternalSecurity = "application-security-external";

    /// <summary>The promotion and the deployment templates both create the production pipeline.</summary>
    internal static bool IsProduction(string name) =>
        PipelineSetupRoles.IsDeployProdTemplate(name) || name == "application-promotion";

    internal static bool IsFastDeploy(string name) => name is "application-release-fast" or "application-light";

    internal static bool IsPackages(string name) =>
        name.StartsWith("package-", StringComparison.Ordinal) || name == "publish-observability-packages";

    /// <summary>A package or external template says what is built; an application template does not say which kind of
    /// application, so the type stays to be asked (null).</summary>
    internal static PipelineArchitecture? Architecture(string name) =>
        IsPackages(name) ? PipelineArchitecture.Package
        : name == ExternalSecurity ? PipelineArchitecture.External
        : null;
}
