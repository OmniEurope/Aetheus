// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Users;

public partial class EntityAuditTrail
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;

    [Parameter, EditorRequired] public string EntityType { get; set; } = string.Empty;
    [Parameter, EditorRequired] public int EntityId { get; set; }

    private List<AuditLogDto> _logs = [];
    private int _count;
    private bool _loading;

    // Recette R-238: the actions the Action filter lists, the same source as the audit log page.
    private List<string> _actionOptions = [];
    private Func<string, string>? _actionFilterText;
    private Func<string, string> ActionFilterText => _actionFilterText ??= ActionText;

    protected override async Task OnInitializedAsync()
    {
        try
        {
            _actionOptions = await Api.Monitoring.GetAuditActionsAsync();
        }
        catch (HttpRequestException)
        {
            _actionOptions = [];
        }
    }

    /// <summary>Recette R2-124: one page of the export, with the grid's sort and column filters.</summary>
    private Task<PaginatedResult<AuditLogDto>> ReadExportAsync(GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest(25);
        var (sortBy, sortDescending) = args.ToSortRequest(nameof(AuditLogDto.Timestamp), fallbackDescending: true);
        return Api.Monitoring.GetAuditLogsAsync(page, pageSize, entityType: EntityType, entityId: EntityId,
            sortBy: sortBy, sortDescending: sortDescending, filters: args.ToApiFilters());
    }

    private async Task LoadDataAsync(GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest(25);
        var (sortBy, sortDescending) = args.ToSortRequest(nameof(AuditLogDto.Timestamp), fallbackDescending: true);
        var filters = args.ToApiFilters();
        _loading = true;
        try
        {
            var result = await Api.Monitoring.GetAuditLogsAsync(page, pageSize, entityType: EntityType, entityId: EntityId,
                sortBy: sortBy, sortDescending: sortDescending, filters: filters);
            _logs = result.Items;
            _count = result.TotalCount;
        }
        finally
        {
            _loading = false;
        }
    }

    // Recette R-452: one label and one colour per audit action, as in the audit log.
    private string ActionText(string action) => Audit.AuditActionPresentation.Label(L, action);

    private Task ShowDetailsAsync(AuditLogDto log) =>
        Dialog.OpenAsync<Aetheus.Front.Components.Audit.AuditDetailDialog>(L["AuditEntryDetail"],
            new Dictionary<string, object?> { ["Log"] = log },
            new OmniDialogOptions
            {
                Width = "700px",
                CloseDialogOnOverlayClick = true,
                AutoFocusFirstElement = false
            });

    private static OmniTone GetActionBadge(string action) => Audit.AuditActionPresentation.Badge(action);
}
