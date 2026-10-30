// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

/// <summary>
/// The organization switcher of the Administration section (owner decision 2026-10-28): the organizations the
/// signed-in user administers (every one for a site Admin, from <c>api/organizations/administered</c>) and the
/// one chosen, which filters the section's pages. No choice (null) means all of them. Reloaded when another
/// user signs in. Pages listen to <see cref="Changed"/>.
/// </summary>
public sealed class AdministrationScopeService(ApiClient api, AuthStateProvider auth)
{
    private int? _loadedFor;

    public IReadOnlyList<AdministeredOrganizationDto> Organizations { get; private set; } = [];

    /// <summary>The organization chosen; null for all of them.</summary>
    public int? SelectedOrganizationId { get; private set; }

    public event Action? Changed;

    /// <summary>Loads the organizations once per signed-in user; a failed load leaves the list empty.</summary>
    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (_loadedFor is not null && _loadedFor == auth.UserId) return;
        try
        {
            Organizations = await api.Servers.GetAdministeredOrganizationsAsync(ct);
        }
        catch (HttpRequestException)
        {
            Organizations = [];
        }
        _loadedFor = auth.UserId;
        if (SelectedOrganizationId is { } selected && Organizations.All(organization => organization.Id != selected))
            SelectedOrganizationId = null;
    }

    /// <summary>Chooses <paramref name="organizationId"/> (null for all); an organization the user does not
    /// administer is ignored.</summary>
    public void Select(int? organizationId)
    {
        if (organizationId is { } id && Organizations.All(organization => organization.Id != id)) return;
        if (organizationId == SelectedOrganizationId) return;
        SelectedOrganizationId = organizationId;
        Changed?.Invoke();
    }

    /// <summary>The organizations where the user holds <paramref name="organizationKey"/>, the key a page's API
    /// resolves its scope with: a page never offers (or requests) an organization its API would refuse.</summary>
    public IReadOnlyList<AdministeredOrganizationDto> OrganizationsFor(string organizationKey) =>
        [.. Organizations.Where(organization => organization.Holds(organizationKey))];

    /// <summary>The chosen organization as <paramref name="organizationKey"/>'s page sees it: the choice when the
    /// user holds that key there, else null (all), so a choice made on another page never reaches this page's API.</summary>
    public int? SelectedFor(string organizationKey) =>
        SelectedOrganizationId is { } selected && Organizations.Any(organization => organization.Id == selected && organization.Holds(organizationKey))
            ? selected
            : null;

    /// <summary>The organization a page that works on one organization at a time uses: the one chosen (when valid
    /// for the page), else the first one holding the key; null when there is none.</summary>
    public int? SelectedOrFirstFor(string organizationKey) =>
        SelectedFor(organizationKey) ?? OrganizationsFor(organizationKey).FirstOrDefault()?.Id;
}
