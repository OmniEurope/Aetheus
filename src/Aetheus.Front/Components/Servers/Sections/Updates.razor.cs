// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Servers.Sections;

/// <summary>Redirects the former packages and updates page to its tab of the services page.</summary>
public partial class Updates
{
    [Parameter] public int Id { get; set; }

    protected override void OnParametersSet() =>
        Nav.NavigateTo($"/servers/{Id}/services?tab=updates", replace: true);
}
