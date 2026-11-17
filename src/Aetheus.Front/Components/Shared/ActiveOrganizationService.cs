// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Shared.Components.Organizations;
using Aetheus.Shared.Components.Shared;
namespace Aetheus.Front.Components.Shared;

/// <summary>
/// Tracks the organizations the user may work in and the currently active one. A member may choose among its
/// memberships (<c>/api/organizations/me</c>); a site Admin, who belongs to no organization by default, may
/// choose among every organization the server lists as administered, or "all of them" (<see cref="Active"/> null,
/// <see cref="AllOrganizationsId"/> in the preference). The active org id is a user preference (R1-66), so it
/// survives reloads. UI components subscribe to <see cref="Changed"/> to react to switches.
/// The client only narrows what the server lists: each endpoint that takes an organization still refuses a
/// caller who is neither a member of it nor a site Admin, whatever this service holds.
/// </summary>
public sealed class ActiveOrganizationService(
    ApiClient api, IUserPreferences preferences, AuthStateProvider auth, ILogger<ActiveOrganizationService>? logger = null)
{
    /// <summary>The choice that stands for every organization (a site Admin only): the preference value and
    /// the option's value in the pickers.</summary>
    public const int AllOrganizationsId = 0;

    public IReadOnlyList<MyOrganizationDto> Available { get; private set; } = [];
    public MyOrganizationDto? Active { get; private set; }
    public bool IsLoaded { get; private set; }

    /// <summary>True for a site Admin: "all organizations" is a valid choice, not only one organization.</summary>
    public bool CanChooseAll => auth.IsAdmin;

    public event Action? Changed;

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var memberships = await api.Servers.GetMyOrganizationsAsync(ct).ConfigureAwait(false) ?? [];
        Available = auth.IsAdmin ? await AllOrganizationsAsync(memberships, ct).ConfigureAwait(false) : memberships;

        var stored = await TryGetStoredAsync().ConfigureAwait(false);
        Active = auth.IsAdmin && stored == AllOrganizationsId
            ? null
            : Available.FirstOrDefault(o => o.Id == stored) ?? memberships.FirstOrDefault();
        IsLoaded = true;
        Changed?.Invoke();
    }

    /// <summary>Chooses <paramref name="organizationId"/>, or every organization with
    /// <see cref="AllOrganizationsId"/> (a site Admin only). An organization that is not in
    /// <see cref="Available"/> is ignored.</summary>
    public async Task SetActiveAsync(int organizationId)
    {
        if (organizationId == AllOrganizationsId)
        {
            if (!auth.IsAdmin || Active is null) return;
            Active = null;
            await preferences.SetAsync(PreferenceKeys.ActiveOrganization, AllOrganizationsId).ConfigureAwait(false);
            Changed?.Invoke();
            return;
        }
        var match = Available.FirstOrDefault(o => o.Id == organizationId);
        if (match is null || (Active is not null && Active.Id == match.Id)) return;
        Active = match;
        await preferences.SetAsync(PreferenceKeys.ActiveOrganization, match.Id).ConfigureAwait(false);
        Changed?.Invoke();
    }

    public void Clear()
    {
        Available = [];
        Active = null;
        IsLoaded = false;
        Changed?.Invoke();
    }

    /// <summary>A site Admin's choices: the organizations the server lists as administered (every one for a site
    /// Admin), the memberships keeping their own role. A refused or failed list leaves the memberships alone, and
    /// says so in the log: the picker then offers fewer organizations than the Admin may reach.</summary>
    private async Task<IReadOnlyList<MyOrganizationDto>> AllOrganizationsAsync(IReadOnlyList<MyOrganizationDto> memberships, CancellationToken ct)
    {
        try
        {
            var administered = await api.Servers.GetAdministeredOrganizationsAsync(ct).ConfigureAwait(false);
            return
            [
                .. administered
                    .Select(organization => memberships.FirstOrDefault(m => m.Id == organization.Id)
                        ?? new MyOrganizationDto(organization.Id, organization.Name, string.Empty, OrganizationRole.Owner))
                    .Concat(memberships)
                    .DistinctBy(organization => organization.Id)
                    .OrderBy(organization => organization.Name, StringComparer.CurrentCultureIgnoreCase)
            ];
        }
        catch (HttpRequestException exception)
        {
            logger?.LogWarning(exception, "[Organizations] The administered organizations could not be read; the picker offers the memberships only");
            return memberships;
        }
    }

    private Task<int?> TryGetStoredAsync() => preferences.GetAsync<int?>(PreferenceKeys.ActiveOrganization);
}
