// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Projects.Sections;

public partial class Edit
{
    [Parameter] public int Id { get; set; }

    // Injected directly (scoped DI) rather than cascaded from ProjectDetailLayout: this page is
    // standalone, so it owns the load itself.
    [Inject] private ProjectDetailLoader Loader { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private Aetheus.Front.Components.Shared.BreadcrumbService Breadcrumb { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;

    protected override async Task OnParametersSetAsync()
    {
        await Loader.EnsureLoadedAsync(Id);
        // Sets the breadcrumb project name directly because this page does not use
        // ProjectDetailLayout.
        if (Loader.Project is { } project)
            Breadcrumb.Set(
                new Aetheus.Front.Components.Shared.BreadcrumbItem(L["Projects"], "/projects"),
                new Aetheus.Front.Components.Shared.BreadcrumbItem(project.Name, $"/projects/{Id}/overview"),
                new Aetheus.Front.Components.Shared.BreadcrumbItem(L["Edit"]));
    }

    // ProjectEditSection raises OnSaved after a successful update. Force-reload the shared (scoped)
    // loader BEFORE navigating so the overview header/sections reflect the edit - a plain navigate
    // would hit EnsureLoadedAsync's same-id no-op and render the stale pre-edit project.
    private async Task OnSaved()
    {
        await Loader.ForceReloadAsync(Id);
        Nav.NavigateTo($"/projects/{Id}/overview");
    }

    // Recette R2-122: the dialog counts what goes with the project, asks for its name and deletes it.
    private async Task DeleteAsync()
    {
        if (!await ProjectDeleteDialog.OpenAsync(Dialog, L, Id)) return;
        Nav.NavigateTo("/projects");
    }
}
