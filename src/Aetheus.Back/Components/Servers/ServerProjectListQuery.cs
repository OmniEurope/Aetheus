// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Servers;

/// <summary> The column filters of a server's projects grid. The keys are the grid's column keys.
/// </summary>
internal static class ServerProjectListQuery
{
    internal static readonly GridQueryMap<Project> Columns = new GridQueryMap<Project>()
        .Text("name", p => p.Name)
        .Enum("status", p => p.Status)
        .Date("createdAt", p => p.CreatedAt);
}
