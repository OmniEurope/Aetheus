// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>The suffixes of the pipeline names the wizard builds (<c>&lt;prefix&gt;-&lt;suffix&gt;</c>).</summary>
internal static class PipelineSetupRoles
{
    public const string Ci = "ci";
    public const string Quality = "quality";
    public const string Security = "security";
    public const string Qa = "qa";
    public const string QaExtended = "qa-extended";
    public const string SecurityHistory = "security-history";
    public const string Candidate = "candidate";
    public const string DeployProd = "deploy-prod";
    /// <summary>The production deployment of a monolith (the Full-stack tile): one host.</summary>
    public const string DeployProdTemplate = "application-deploy-prod";
    /// <summary>The production deployment of a front and an API (R-11): a host, a certificate and virtual hosts for each.</summary>
    public const string DeployProdFrontBackTemplate = "application-deploy-prod-front-back";
    public const string QaTemplate = "application-qa";
    public const string QaExtendedTemplate = "application-qa-extended";
    public const string ReleaseFastTemplate = "application-release-fast";
    public const string PackageTelemetry = "package-telemetry";
    public const string PackageWebAnalyticsBrowser = "package-web-analytics-browser";

    /// <summary>R-11: the production deployment template of the application shape the user chose, never deduced from the
    /// Compose file: the Full-stack tile (<paramref name="fullStack"/> true) is a monolith, the other one a front and an API.</summary>
    public static string DeployProdTemplateFor(bool? fullStack) =>
        fullStack == true ? DeployProdTemplate : DeployProdFrontBackTemplate;

    public static bool IsDeployProdTemplate(string templateName) =>
        templateName is DeployProdTemplate or DeployProdFrontBackTemplate;

    /// <summary>R-37: the templates whose libraries and vaults the summary asks for. A fast release created without the
    /// production deployment still prepares the production setup (Compose file, ports and secrets written into the library and
    /// the vault <c>&lt;slug&gt;.prod</c>), and only the production deployment template declares that vault: it is added to
    /// the list, so the vault is offered by the rule that already offers it when production is ticked, never by a second one.</summary>
    public static IReadOnlyList<string> ReadinessTemplates(IReadOnlyList<string> selected, bool? fullStack) =>
        selected.Contains(ReleaseFastTemplate) && !selected.Any(IsDeployProdTemplate)
            ? [.. selected, DeployProdTemplateFor(fullStack)]
            : selected;
}
