// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Net.Http;

namespace Aetheus.Front.Components.Mail;

/// <summary>Recette R1-41: the mailboxes of every server the reader sees; a row opens its integrated mailbox.</summary>
public partial class MailboxDirectory
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private BreadcrumbService Breadcrumb { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    private AetheusDataGrid<MailboxDirectoryEntryDto>? _grid;
    private List<MailboxDirectoryEntryDto> _items = [];
    private int _total;
    private bool _loading;

    protected override void OnInitialized() => Breadcrumb.Set(new BreadcrumbItem(L["MailboxDirectory"]));

    internal async Task LoadAsync(GridLoadArgs args)
    {
        _loading = true;
        try
        {
            var page = await ReadAsync(args, 25);
            if (page is null)
            {
                Toast.Error(L["LoadFailed"]);
                return;
            }
            _items = page.Items;
            _total = page.TotalCount;
        }
        catch (HttpRequestException)
        {
            // An expired session is redirected to the login page; the list says the read failed meanwhile.
            Toast.Error(L["LoadFailed"]);
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>The export reads the list page by page from the backend, as the grid does.</summary>
    internal async Task<PaginatedResult<MailboxDirectoryEntryDto>> ReadExportAsync(GridLoadArgs args) =>
        await ReadAsync(args, 200) ?? new();

    internal void Open(MailboxDirectoryEntryDto entry) =>
        Nav.NavigateTo($"/servers/{entry.ServerId}/mail/accounts/{entry.AccountId}/mailbox");

    private Task<PaginatedResult<MailboxDirectoryEntryDto>?> ReadAsync(GridLoadArgs args, int defaultPageSize)
    {
        var (page, pageSize) = args.ToPageRequest(defaultPageSize);
        var (sortBy, descending) = args.ToSortRequest("Email");
        return Api.Mailbox.GetDirectoryAsync(page, pageSize, args.ColumnFilter("Email"), sortBy, descending);
    }

    private string QuotaText(MailboxDirectoryEntryDto entry) => entry.QuotaMb > 0
        ? string.Format(CultureInfo.CurrentCulture, L["MailboxDirectoryQuota"], entry.UsedMb, entry.QuotaMb)
        : string.Format(CultureInfo.CurrentCulture, L["MailboxDirectoryUsed"], entry.UsedMb);
}
