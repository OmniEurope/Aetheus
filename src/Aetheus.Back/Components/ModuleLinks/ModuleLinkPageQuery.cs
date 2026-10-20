// SPDX-License-Identifier: EUPL-1.2
using System.Linq.Expressions;
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.ModuleLinks;

/// <summary> The column filters of a resource's links grid. A row shows the other side of the link
/// (the target when the resource is the source, the source otherwise), so the Type and Resource
/// keys read that side; the map is built per request because which side is "the other one" depends
/// on it. </summary>
internal static class ModuleLinkPageQuery
{
    internal static GridQueryMap<ModuleLink> Columns(ModuleLinkType sourceType, List<string> identifiers)
    {
        Expression<Func<ModuleLink, ModuleLinkType>> otherType = link =>
            link.SourceType == sourceType && identifiers.Contains(link.SourceIdentifier) ? link.TargetType : link.SourceType;
        Expression<Func<ModuleLink, string?>> otherIdentifier = link =>
            link.SourceType == sourceType && identifiers.Contains(link.SourceIdentifier) ? link.TargetIdentifier : link.SourceIdentifier;
        return new GridQueryMap<ModuleLink>()
            .Enum("type", otherType)
            .Text("identifier", otherIdentifier)
            .Boolean("isAutoDetected", link => link.IsAutoDetected);
    }
}
