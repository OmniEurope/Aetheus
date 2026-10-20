// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.ServiceConnections;

/// <summary>The column header filters of the service connections list; each key is the grid
/// column's key.</summary>
internal static class ServiceConnectionListQuery
{
    internal static readonly GridQueryMap<ServiceConnection> Columns = new GridQueryMap<ServiceConnection>()
        .Text("name", connection => connection.Name)
        .Enum("type", connection => connection.Type)
        .Text("url", connection => connection.Url)
        .Text("projectName", connection => connection.Project == null ? null : connection.Project.Name);
}
