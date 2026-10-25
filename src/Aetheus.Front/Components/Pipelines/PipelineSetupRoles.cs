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
    public const string DeployProdTemplate = "application-deploy-prod";
    public const string QaTemplate = "application-qa";
    public const string ReleaseFastTemplate = "application-release-fast";
    public const string PackageTelemetry = "package-telemetry";
    public const string PackageWebAnalyticsBrowser = "package-web-analytics-browser";
}
