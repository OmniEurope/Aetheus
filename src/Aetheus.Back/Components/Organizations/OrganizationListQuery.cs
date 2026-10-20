// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Organizations;

/// <summary> The column filters of the organizations list. The keys are the grid's column keys; the
/// member and project counts are compared as the grid shows them. </summary>
internal static class OrganizationListQuery
{
    internal static readonly GridQueryMap<Organization> Columns = new GridQueryMap<Organization>()
        .Text("name", o => o.Name)
        .Text("slug", o => o.Slug)
        .Text("description", o => o.Description)
        .Number("memberCount", o => o.Members.Count)
        .Number("projectCount", o => o.Projects.Count);
}
