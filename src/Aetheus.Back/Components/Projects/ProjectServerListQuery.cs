// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Projects;

/// <summary> The column filters of a project's servers section. The keys are the grid's column
/// keys; the status and action columns are not filterable, so they have no entry. </summary>
internal static class ProjectServerListQuery
{
    internal static readonly GridQueryMap<ProjectServer> Columns = new GridQueryMap<ProjectServer>()
        .Text("displayName", s => s.DisplayName)
        .Text("host", s => s.Host)
        .Number("port", s => s.Port)
        .Enum("type", s => s.Type);
}
