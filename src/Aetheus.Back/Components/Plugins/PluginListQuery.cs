// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Plugins;

/// <summary>The column header filters of the plugins list; each key is the grid column's
/// key.</summary>
internal static class PluginListQuery
{
    internal static readonly GridQueryMap<PluginRegistration> Columns = new GridQueryMap<PluginRegistration>()
        .Text("name", plugin => plugin.Name)
        .Text("version", plugin => plugin.Version)
        .Enum("type", plugin => plugin.Type)
        .Text("author", plugin => plugin.Author)
        .Enum("status", plugin => plugin.Status)
        .Date("createdAt", plugin => plugin.CreatedAt);
}
