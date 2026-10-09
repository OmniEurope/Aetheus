// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.AiTasks;

public partial class AiRunnerProfiles : PagedDataGridPageBase<AiRunnerProfileDto>
{
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private AuthStateProvider Auth { get; set; } = default!;

    private List<AiRunnerProfileDto> _profiles = [];

    // Recette R-224: the binaries the Binary column's checkable filter offers, and the yes/no texts.
    private IReadOnlyList<string> _binaries = [];
    private Func<string, string>? _yesNoText;
    private Func<string, string> YesNoText => _yesNoText ??= GridFilterText.YesNo(L);

    protected override async Task OnInitializedAsync()
    {
        if (!Auth.IsAdmin)
        {
            Nav.NavigateTo("/");
            return;
        }
        Breadcrumb.Set(
            new BreadcrumbItem(L["Administration"], "/admin"),
            new BreadcrumbItem(L["AiRunnerProfiles"]));
        await LoadFilterValuesAsync();
    }

    /// <summary>Every binary across the profiles, read from the profile options (an admin sees them all).</summary>
    private async Task LoadFilterValuesAsync()
    {
        try
        {
            var options = await Api.Ai.GetAiRunnerProfileOptionsAsync();
            _binaries = [.. options.Select(profile => profile.Binary)
                .Where(binary => !string.IsNullOrWhiteSpace(binary))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)];
        }
        catch (HttpRequestException)
        {
            _binaries = [];
        }
    }

    internal Task LoadDataAsync(GridLoadArgs args) => LoadPageFromArgsAsync(args);

    /// <summary>Recette R2-124: one page of the export, with the grid's sort and column filters.</summary>
    internal Task<PaginatedResult<AiRunnerProfileDto>> ReadExportAsync(GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest(25);
        var (sortBy, sortDescending) = args.ToSortRequest("Name");
        return Api.Ai.GetAiRunnerProfilesAsync(page, pageSize, filters: args.ToApiFilters(),
            sortBy: sortBy, sortDescending: sortDescending);
    }

    protected override async Task LoadPageAsync(int page, int pageSize)
    {
        var filters = _columnFilters;
        var result = await LoadPageResultAsync(
            () => Api.Ai.GetAiRunnerProfilesAsync(page, pageSize, filters: filters,
                sortBy: _sortBy, sortDescending: _sortDescending));
        _profiles = result.Items;
    }

    private async Task OpenDialogAsync(AiRunnerProfileDto? profile)
    {
        var result = await Dialog.OpenAsync<AiRunnerProfileDialog>(
            profile is null ? L["Create"] : L["Edit"],
            new Dictionary<string, object?> { ["ProfileId"] = profile?.Id },
            new OmniDialogOptions { Width = "760px", CloseDialogOnOverlayClick = true, AutoFocusFirstElement = false });
        if (result is not true) return;
        await LoadFilterValuesAsync();
        await ReloadPageAsync();
    }

    private async Task DeleteAsync(AiRunnerProfileDto profile)
    {
        var confirmed = await Dialog.Confirm(
            string.Format(L["DeleteAiRunnerProfileConfirm"], profile.Name), L["Delete"].Value,
            new OmniConfirmOptions { Destructive = true, OkButtonText = L["Delete"].Value, CancelButtonText = L["GoBack"].Value });
        if (confirmed != true) return;
        await Ui.RunAsync(
            () => Api.Ai.DeleteAiRunnerProfileAsync(profile.Id),
            "Deleted",
            ReloadPageAsync,
            errorKey: "DeleteFailed",
            successTitleKey: "Deleted");
    }
}
