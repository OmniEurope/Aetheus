// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Shared;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>Fleet filters use projected rows because owner and freshness are computed; each map key
/// matches a grid column.</summary>
internal static class PipelineFleetQuery
{
    internal static readonly GridQueryMap<PipelineFleetItemDto> Columns = new GridQueryMap<PipelineFleetItemDto>()
        .Text("pipelineName", item => item.PipelineName)
        .Text("ownerName", item => item.OwnerName)
        .Text("templateName", item => item.TemplateName)
        .Number("pinnedVersion", item => item.PinnedVersion)
        .Number("latestVersion", item => item.LatestVersion)
        .Enum("freshness", item => item.Freshness);
}
