// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

/// <summary>
/// Chooses the organization the Administration section's pages show (owner decision 2026-10-28). Hidden when
/// the user administers a single organization, unless <see cref="ShowWithOneOrganization"/> (a site Admin
/// choosing between the platform's mail templates and one organization's). The value 0 stands for
/// <see cref="AllLabelKey"/> (all organizations, or the platform), offered when <see cref="OfferAll"/>.
/// </summary>
public partial class AdministrationOrganizationSwitcher
{
    [Inject] private AdministrationScopeService Scope { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    /// <summary>The organization key the page's API resolves its scope with; only organizations holding it are listed.</summary>
    [Parameter, EditorRequired] public string PermissionKey { get; set; } = string.Empty;

    /// <summary>Offers the "all" entry (value 0); without it the switcher always names one organization.</summary>
    [Parameter] public bool OfferAll { get; set; } = true;

    /// <summary>The label of the "all" entry.</summary>
    [Parameter] public string AllLabelKey { get; set; } = "AdminScopeAllOrganizations";

    /// <summary>Shown even with one organization, when the "all" entry is a scope of its own.</summary>
    [Parameter] public bool ShowWithOneOrganization { get; set; }

    /// <summary>Raised after the choice changed.</summary>
    [Parameter] public EventCallback Changed { get; set; }

    private IReadOnlyList<AdministeredOrganizationDto> Organizations => Scope.OrganizationsFor(PermissionKey);

    internal bool Visible => Organizations.Count > 1 || (ShowWithOneOrganization && Organizations.Count == 1);

    internal int CurrentValue => (OfferAll ? Scope.SelectedFor(PermissionKey) : Scope.SelectedOrFirstFor(PermissionKey)) ?? 0;

    internal IReadOnlyList<OmniOption<int>> Options =>
    [
        .. OfferAll ? [new OmniOption<int>(0, L[AllLabelKey])] : Array.Empty<OmniOption<int>>(),
        .. Organizations.Select(organization => new OmniOption<int>(organization.Id, organization.Name))
    ];

    protected override Task OnInitializedAsync() => Scope.EnsureLoadedAsync();

    private async Task OnSelectedAsync(int value)
    {
        Scope.Select(value == 0 ? null : value);
        await Changed.InvokeAsync();
    }
}
