// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>The template the backend resolver will run for a name, so the version the wizard pins in
/// <c>extends:</c> is that template's (R-78): the project's organization's own template, otherwise the
/// delivery template the platform shares with every organization, and only then any other one listed.</summary>
internal static class PipelineSetupTemplateChoice
{
    public static PipelineTemplateSummaryDto? For(
        IEnumerable<PipelineTemplateSummaryDto> templates, string name, int? organizationId) => templates
        .Where(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(item => item.OrganizationId == organizationId)
        .ThenByDescending(item => item.IsShared)
        .FirstOrDefault();
}
