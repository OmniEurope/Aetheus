// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.ServerApps;

/// <summary> The column filters of a server's applications grid. The keys are the grid's column
/// keys. </summary>
internal static class ServerAppListQuery
{
    internal static readonly GridQueryMap<ServerApp> Columns = new GridQueryMap<ServerApp>()
        .Text("name", a => a.Name)
        .Enum("status", a => a.Status)
        .Text("version", a => a.Version)
        .Number("port", a => a.Port)
        .Text("source", a => a.Source);
}
