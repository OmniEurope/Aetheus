// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Servers;

/// <summary> The column filters of the retired servers dialog. The keys are the grid's column keys;
/// the retirement date is the soft-delete stamp. </summary>
internal static class RetiredServerListQuery
{
    internal static readonly GridQueryMap<Server> Columns = new GridQueryMap<Server>()
        .Text("name", s => s.Name)
        .Text("hostname", s => s.Hostname)
        .Date("retiredAt", s => s.DeletedAt);
}
