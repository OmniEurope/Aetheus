// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Shared.Components.Organizations;
namespace Aetheus.Front.Components.Shared;

/// <summary>
/// Tracks the user's available organizations (loaded from <c>/api/organizations/me</c>) and
/// the currently active organization. The active org id is a user preference (R1-66), so it
/// survives reloads. UI components subscribe to <see cref="Changed"/> to react to switches.
/// </summary>
public sealed class ActiveOrganizationService(ApiClient api, IUserPreferences preferences)
{

    public IReadOnlyList<MyOrganizationDto> Available { get; private set; } = [];
    public MyOrganizationDto? Active { get; private set; }
    public bool IsLoaded { get; private set; }

    public event Action? Changed;

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var orgs = await api.Servers.GetMyOrganizationsAsync(ct).ConfigureAwait(false) ?? [];
        Available = orgs;

        var stored = await TryGetStoredAsync().ConfigureAwait(false);
        Active = orgs.FirstOrDefault(o => o.Id == stored) ?? orgs.FirstOrDefault();
        IsLoaded = true;
        Changed?.Invoke();
    }

    public async Task SetActiveAsync(int organizationId)
    {
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

    private Task<int?> TryGetStoredAsync() => preferences.GetAsync<int?>(PreferenceKeys.ActiveOrganization);
}
