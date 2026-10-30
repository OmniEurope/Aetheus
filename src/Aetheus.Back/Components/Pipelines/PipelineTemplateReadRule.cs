// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// R-78: the delivery templates the platform organization ships are the fallback of every organization
/// that defines none of its own (the template resolver extends them by name). They ship with the product
/// and hold no secret, so a user who may read pipeline templates in at least one organization may read
/// them too. Read only: changing one stays with the rights of the platform organization. Any other
/// template keeps the rights of the organization that owns it.
/// </summary>
internal static class PipelineTemplateReadRule
{
    /// <summary>True when the user may read the shared delivery templates: a role held for at least
    /// one organization grants Read on every pipeline template of that organization.</summary>
    internal static async Task<bool> MayReadSharedCatalogAsync(
        IResourceAuthorizationService authz, ClaimsPrincipal user, CancellationToken ct) =>
        (await authz.GetOrganizationIdsWithPermissionAsync(
            user, ResourceType.PipelineTemplate, Permission.Read, ct).ConfigureAwait(false)).Count > 0;

    /// <summary>Read on the template itself, or a shared delivery template and
    /// <see cref="MayReadSharedCatalogAsync"/>.</summary>
    internal static async Task<bool> CanReadAsync(
        IResourceAuthorizationService authz, IPipelineTemplateService templates,
        ClaimsPrincipal user, int templateId, CancellationToken ct) =>
        await authz.HasPermissionAsync(user, ResourceType.PipelineTemplate, templateId, Permission.Read, ct)
            .ConfigureAwait(false)
        || (await templates.IsSharedCatalogTemplateAsync(templateId, ct).ConfigureAwait(false)
            && await MayReadSharedCatalogAsync(authz, user, ct).ConfigureAwait(false));
}
