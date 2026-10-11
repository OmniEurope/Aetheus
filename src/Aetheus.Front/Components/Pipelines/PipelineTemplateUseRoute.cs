// SPDX-License-Identifier: EUPL-1.2
using System.Collections.Frozen;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// Where a template's "Use" button leads (D9, SITE-FUNC-TEMPLATE-USE-001). The setup wizard builds a
/// project's set of role pipelines and can only preselect the built-in role templates it knows by
/// name; any other template (one a user wrote) opened there left every role unchecked. Such a
/// template opens the pipeline editor prefilled from it instead.
/// </summary>
internal static class PipelineTemplateUseRoute
{
    // The names PipelineSetupWizard.SelectOnlyRequestedTemplate maps to a role. Kept in step with it
    // by PipelineTemplateUseRouteTests, which asks the wizard to preselect each of them.
    internal static readonly FrozenSet<string> WizardRoleTemplates = new[]
    {
        "application-ci",
        "application-quality",
        "application-security",
        "application-qa",
        "application-qa-extended",
        "application-security-history",
        "application-security-external",
        "application-candidate",
        "application-nightly",
        "application-deploy-prod",
        "application-promotion",
        "application-release-fast",
        "application-light",
        "package-telemetry",
        "package-web-analytics-dotnet",
        "package-web-analytics-browser",
        "publish-observability-packages"
    }.ToFrozenSet(StringComparer.Ordinal);

    public static string For(int templateId, string templateName) =>
        WizardRoleTemplates.Contains(templateName)
            ? $"/pipelines/setup?templateId={templateId}"
            : $"/pipelines/new?templateId={templateId}";
}
