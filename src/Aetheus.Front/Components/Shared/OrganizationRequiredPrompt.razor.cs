// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

/// <summary>The in-page organization choice of a page that needs an active organization (R-34).</summary>
public partial class OrganizationRequiredPrompt
{
    [Inject] private ActiveOrganizationService Orgs { get; set; } = default!;
    [Inject] private ListCacheService Cache { get; set; } = default!;

    /// <summary>What the page says while no organization is active.</summary>
    [Parameter, EditorRequired] public string Message { get; set; } = string.Empty;

    /// <summary>The Material name of the icon above the message; the organization's by default.</summary>
    [Parameter] public string Icon { get; set; } = "apartment";

    private Task OnSelectedAsync(int organizationId)
    {
        // Org-scoped lists belong to the previous organization: drop the cache, as the account menu's switch does.
        Cache.Clear();
        return Orgs.SetActiveAsync(organizationId);
    }
}
