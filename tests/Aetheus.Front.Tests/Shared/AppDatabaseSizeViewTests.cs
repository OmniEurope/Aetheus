// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Aetheus.Front.Tests.Shared;

/// <summary>The Database tab presents measured size, largest tables, estimated rows and change over
/// 24 hours.</summary>
public sealed class AppDatabaseSizeViewTests : BunitContext
{
    private const long Mb = 1024 * 1024;
    private readonly BunitTestHelper.TestHandler _handler;

    public AppDatabaseSizeViewTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        // The units are localized: this localizer gives them their English text, every other key its name.
        Services.AddSingleton<IStringLocalizer<Aetheus.Front.Resources.AppStrings>>(new UnitsLocalizer());
    }

    [Fact]
    public void TheTotal_TheLargestTables_AndTheirTrend_AreShown()
    {
        _handler.SetJsonResponse("api/appmonitoring/apps/7/database", new AppDatabaseSizeReportDto
        {
            MeasuredAt = new DateTime(2026, 10, 4, 10, 0, 0, DateTimeKind.Utc),
            DatabaseName = "aetheus",
            TotalBytes = 6290 * Mb,
            TotalBytesDayBefore = 6250 * Mb,
            Tables =
            [
                new AppDatabaseTableSizeDto { Schema = "public", Name = "AppMetricSamples", TotalBytes = 3049 * Mb, EstimatedRows = 6_631_915, TotalBytesDayBefore = 3100 * Mb },
                new AppDatabaseTableSizeDto { Schema = "public", Name = "AnalysisMetrics", TotalBytes = 2425 * Mb, EstimatedRows = 8_418_471, TotalBytesDayBefore = 2425 * Mb },
                new AppDatabaseTableSizeDto { Schema = "audit", Name = "Fresh", TotalBytes = 16 * 1024, EstimatedRows = null, TotalBytesDayBefore = null }
            ]
        });

        var cut = Render<AppDatabaseSizeView>(parameters => parameters.Add(component => component.AppId, 7));

        cut.WaitForAssertion(() => Assert.Contains("AppMetricSamples", cut.Markup, StringComparison.Ordinal));
        var total = cut.Find("[data-testid='app-database-total']").TextContent;
        Assert.Contains(DisplayFormatting.Size(6290 * Mb), total, StringComparison.Ordinal);
        Assert.Contains("+" + DisplayFormatting.Size(40 * Mb) + " over 24 hours", total, StringComparison.Ordinal);
        var largest = cut.Find("[data-testid='app-database-largest']").TextContent;
        Assert.Contains(DisplayFormatting.Size(3049 * Mb), largest, StringComparison.Ordinal);
        Assert.Contains("AppMetricSamples", largest, StringComparison.Ordinal);
        Assert.Contains("aetheus", cut.Find("[data-testid='app-database-measured']").TextContent, StringComparison.Ordinal);

        Assert.Contains(N(8_418_471), cut.Markup, StringComparison.Ordinal);
        Assert.Contains("audit.Fresh", cut.Markup, StringComparison.Ordinal); // a schema other than public is named
        Assert.Contains("16 Ko", cut.Markup, StringComparison.Ordinal);
        var trends = cut.FindAll("[data-testid='app-database-trend']").Select(cell => cell.TextContent).ToList();
        Assert.Equal(["-" + DisplayFormatting.Size(51 * Mb), "0 o", "-"], trends);
    }

    [Fact]
    public void WithoutAMeasurement_TheTabSaysWhereItComesFrom()
    {
        _handler.SetJsonResponse("api/appmonitoring/apps/7/database", new AppDatabaseSizeReportDto());

        var cut = Render<AppDatabaseSizeView>(parameters => parameters.Add(component => component.AppId, 7));

        cut.WaitForAssertion(() => Assert.Contains("AppDatabaseNoDataHint", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("[data-testid='app-database-total']"));
    }

    [Fact]
    public void WithoutAMeasurementADayAgo_TheTotalSaysSo()
    {
        _handler.SetJsonResponse("api/appmonitoring/apps/7/database", new AppDatabaseSizeReportDto
        {
            MeasuredAt = new DateTime(2026, 10, 4, 10, 0, 0, DateTimeKind.Utc),
            TotalBytes = 10 * Mb
        });

        var cut = Render<AppDatabaseSizeView>(parameters => parameters.Add(component => component.AppId, 7));

        cut.WaitForAssertion(() => Assert.Contains("AppDatabaseTrendUnknown",
            cut.Find("[data-testid='app-database-total']").TextContent, StringComparison.Ordinal));
    }

    private static string N(long value) => value.ToString("N0", CultureInfo.CurrentCulture);

    private sealed class UnitsLocalizer : IStringLocalizer<Aetheus.Front.Resources.AppStrings>
    {
        private static readonly Dictionary<string, string> Texts = new(StringComparer.Ordinal)
        {
            ["SizeMegabytes"] = "{0} MB",
            ["SizeKilobytes"] = "{0} kB",
            ["AppDatabaseTrendDay"] = "{0} over 24 hours"
        };

        public LocalizedString this[string name] => new(name, Texts.GetValueOrDefault(name, name));
        public LocalizedString this[string name, params object[] arguments] =>
            new(name, string.Format(CultureInfo.InvariantCulture, Texts.GetValueOrDefault(name, name), arguments));
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }
}
