// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Shared;

/// <summary>The entities whose displayed name links to their page.</summary>
public enum EntityNameKind
{
    Server,
    Project,
    Pipeline,
    Environment,
    VariableLibrary,
    Vault,
    Organization,
}

public partial class EntityNameLink
{
    [Parameter, EditorRequired] public EntityNameKind Kind { get; set; }

    /// <summary>Id of the entity; null leaves the name as plain text.</summary>
    [Parameter] public int? Id { get; set; }

    [Parameter] public string? Name { get; set; }

    public static string Href(EntityNameKind kind, int id) => kind switch
    {
        EntityNameKind.Server => $"/servers/{id}/overview",
        EntityNameKind.Project => $"/projects/{id}/overview",
        EntityNameKind.Pipeline => $"/pipelines/{id}",
        EntityNameKind.Environment => $"/environments/{id}",
        EntityNameKind.VariableLibrary => $"/variable-libraries/{id}",
        EntityNameKind.Vault => $"/vaults/{id}",
        EntityNameKind.Organization => $"/admin/organizations/{id}",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
