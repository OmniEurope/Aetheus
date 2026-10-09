// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace Aetheus.Front.Components.Shared;

public partial class AppDatabaseSizeView
{
    private const double Megabyte = 1024d * 1024d;

    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter] public int AppId { get; set; }

    private AppDatabaseSizeReportDto? _report;
    private bool _loading = true;
    private bool _loadFailed;
    private int _lastAppId = -1;

    protected override async Task OnParametersSetAsync()
    {
        if (AppId == _lastAppId) return;
        _lastAppId = AppId;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _loading = true;
        _loadFailed = false;
        try
        {
            _report = await Api.Monitoring.GetAppDatabaseSizeAsync(AppId);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            _report = null;
            _loadFailed = true;
        }
        finally
        {
            _loading = false;
        }
    }

    private AppDatabaseTableSizeDto? Largest => _report?.Tables.FirstOrDefault();

    private string TotalText => Bytes(_report?.TotalBytes);

    private string TotalTrendText => _report?.TotalBytesDayBefore is null
        ? L["AppDatabaseTrendUnknown"]
        : string.Format(CultureInfo.CurrentCulture, L["AppDatabaseTrendDay"],
            Trend(_report.TotalBytes, _report.TotalBytesDayBefore));

    private string LargestSizeText => Bytes(Largest?.TotalBytes);

    private string? LargestNameText => Largest is { } table ? TableName(table) : null;

    private string MeasuredAtText => _report?.MeasuredAt is { } measuredAt
        ? measuredAt.ToString("g", CultureInfo.CurrentCulture)
        : L["Never"];

    private string? DatabaseNameText => _report?.DatabaseName;

    /// <summary>The schema is written only when it is not the default one.</summary>
    internal static string TableName(AppDatabaseTableSizeDto table) =>
        string.IsNullOrEmpty(table.Schema) || table.Schema == "public" ? table.Name : $"{table.Schema}.{table.Name}";

    /// <summary>Megabytes of 1,048,576 bytes, the unit of <c>pg_size_pretty</c>, so the figures read like
    /// the <c>psql</c> measurement; under one megabyte, kilobytes.</summary>
    internal string Bytes(long? bytes) => bytes switch
    {
        null => "-",
        < 1024 * 1024 => string.Format(CultureInfo.CurrentCulture, L["SizeKilobytes"], (bytes.Value / 1024d).ToString("N0", CultureInfo.CurrentCulture)),
        _ => string.Format(CultureInfo.CurrentCulture, L["SizeMegabytes"], (bytes.Value / Megabyte).ToString("N0", CultureInfo.CurrentCulture))
    };

    internal static string Rows(long? rows) => rows?.ToString("N0", CultureInfo.CurrentCulture) ?? "-";

    /// <summary>"+12 MB", "-3 MB", or "-" without an earlier measurement.</summary>
    internal string Trend(long? now, long? before)
    {
        if (now is null || before is null) return "-";
        var change = now.Value - before.Value;
        var text = Bytes(Math.Abs(change));
        return change switch
        {
            > 0 => "+" + text,
            < 0 => "-" + text,
            _ => text
        };
    }
}
