// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

/// <summary>
/// The selector of the active organization (R-33, R-34). In the account menu it offers "all organizations" to a
/// site Admin (<see cref="OfferAll"/>); in a page that needs one organization it lists the organizations only,
/// with an empty entry while none is chosen. The choice is handed to <see cref="OnSelected"/> (the host switches
/// the organization and drops what belonged to the previous one); an emptied choice is ignored.
/// </summary>
public partial class ActiveOrganizationPicker
{
    [Inject] private ActiveOrganizationService Orgs { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    /// <summary>Offers "all organizations" (value 0) first, when the user is a site Admin.</summary>
    [Parameter] public bool OfferAll { get; set; }

    [Parameter] public string? Id { get; set; }

    [Parameter] public string? Class { get; set; }

    /// <summary>Raised with the organization id chosen (<see cref="ActiveOrganizationService.AllOrganizationsId"/> for all).</summary>
    [Parameter, EditorRequired] public EventCallback<int> OnSelected { get; set; }

    private bool ShowsAll => OfferAll && Orgs.CanChooseAll;

    private int? ActiveId => Orgs.Active?.Id;

    private int? AllValue => Orgs.Active?.Id ?? ActiveOrganizationService.AllOrganizationsId;

    private IReadOnlyList<OmniOption<int?>> Options =>
        [.. Orgs.Available.Select(organization => new OmniOption<int?>(organization.Id, organization.Name))];

    private IReadOnlyList<OmniOption<int?>> AllOptions =>
    [
        new OmniOption<int?>(ActiveOrganizationService.AllOrganizationsId, L["AllOrganizations"]),
        .. Options
    ];

    private Task OnChosenAsync(int? id) => id is { } chosen ? OnSelected.InvokeAsync(chosen) : Task.CompletedTask;
}
