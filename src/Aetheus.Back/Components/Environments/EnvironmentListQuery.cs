// SPDX-License-Identifier: EUPL-1.2
using Environment = Aetheus.Back.Data.Entities.Environment;

namespace Aetheus.Back.Components.Environments;

/// <summary>The column filters of the environments list (global and project scopes).</summary>
internal static class EnvironmentListQuery
{
    internal static readonly GridQueryMap<Environment> Columns = new GridQueryMap<Environment>()
        .Text("name", e => e.Name)
        .Text("description", e => e.Description)
        .Enum("type", e => e.Type)
        .Boolean("requireApproval", e => e.RequireApproval)
        .Date("updatedAt", e => e.UpdatedAt);
}
