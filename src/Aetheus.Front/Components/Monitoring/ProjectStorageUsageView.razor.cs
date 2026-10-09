// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace Aetheus.Front.Components.Monitoring;

/// <summary>
/// PLAN-013 lot 5: the storage of each project the caller can read, in the Supervision. Over 80 % the
/// share turns orange, at the budget red: the budget sweep then purges the oldest detail.
/// </summary>
public partial class ProjectStorageUsageView
{
    private const double Megabyte = 1024d * 1024d;

    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    private ProjectStorageReportDto? _report;
    private bool _loading = true;
    private bool _loadFailed;

    protected override async Task OnInitializedAsync()
    {
        try
        {
            _report = await Api.Monitoring.GetProjectStorageAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            _loadFailed = true;
        }
        _loading = false;
    }

    private string Note => _report?.MeasuredAt is { } measuredAt
        ? string.Format(CultureInfo.CurrentCulture, L["ProjectStorageMeasuredNote"], measuredAt.ToString("g", CultureInfo.CurrentCulture))
        : L["ProjectStorageEstimatedNote"];

    internal string Megabytes(long bytes) =>
        string.Format(CultureInfo.CurrentCulture, L["SizeMegabytes"], (bytes / Megabyte).ToString("N1", CultureInfo.CurrentCulture));

    internal static string Percent(int percent) => string.Create(CultureInfo.CurrentCulture, $"{percent} %");

    internal static OmniTone ShareTone(int percent) =>
        percent >= 100 ? OmniTone.Danger : percent >= ProjectStorageDefaults.AlertPercent ? OmniTone.Warning : OmniTone.Neutral;

    internal static string LargestTable(ProjectStorageUsageDto usage) =>
        usage.Tables.FirstOrDefault(table => table.Bytes > 0)?.Table ?? "-";
}
