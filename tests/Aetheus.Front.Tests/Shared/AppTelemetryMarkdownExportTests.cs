// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Tests.Shared;

/// <summary>Telemetry export tests cover header lines, displayed column values and filenames;
/// localized text is represented by resource keys in assertions.</summary>
public sealed class AppTelemetryMarkdownExportTests
{
    private static string Text(string key) => key switch
    {
        "LogsExportPeriodValue" => "last {0} hours",
        _ => key
    };

    [Fact]
    public void LogsFields_StateTheApplicationAndThePeriod()
    {
        var fields = AppTelemetryMarkdownExport.LogsFields(3, "Shop API", 24, Text);

        Assert.Equal([("Application", "Shop API (#3)"), ("Period", "last 24 hours")],
            fields.Select(field => (field.Label, field.Value)));
    }

    [Fact]
    public void ErrorsFields_WithoutAName_ShowTheId()
    {
        var fields = AppTelemetryMarkdownExport.ErrorsFields(7, null, Text);

        Assert.Equal([("Application", "#7"), ("Period", "ErrorsExportPeriodValue")],
            fields.Select(field => (field.Label, field.Value)));
    }

    [Fact]
    public void Title_PutsTheApplicationInTheFormat() =>
        Assert.Equal("Logs of Shop API (#3)", AppTelemetryMarkdownExport.Title("Logs of {0}", 3, "Shop API"));

    [Fact]
    public void SeverityExport_WritesTheLabelThenTheOtlpNumber()
    {
        Assert.Equal("[LogSeverityError] (17)", AppTelemetryMarkdownExport.SeverityExport(new AppLogEntryDto { SeverityNumber = 17, SeverityText = "Error" }, Bracket));
        Assert.Equal("[LogSeverityWarn] (13)", AppTelemetryMarkdownExport.SeverityExport(new AppLogEntryDto { SeverityNumber = 13 }, Bracket));
    }

    [Theory]
    [InlineData(1, "Trace", "[LogSeverityTrace]")]
    [InlineData(8, "DEBUG", "[LogSeverityDebug]")]
    [InlineData(9, "Information", "[LogSeverityInfo]")]
    [InlineData(13, "Warning", "[LogSeverityWarn]")]
    [InlineData(16, "warn", "[LogSeverityWarn]")]
    [InlineData(17, "Error", "[LogSeverityError]")]
    [InlineData(24, "Critical", "[LogSeverityFatal]")]
    [InlineData(0, "Custom", "Custom")]
    [InlineData(0, null, "-")]
    public void R2012_SeverityLabel_IsTheLocalizedClassOfTheNumber_NotTheRawSeverityText(int number, string? text, string expected) =>
        Assert.Equal(expected, AppTelemetryMarkdownExport.SeverityLabel(
            new AppLogEntryDto { SeverityNumber = number, SeverityText = text }, Bracket));

    [Fact]
    public void R2012_EveryFilterClass_HasTheLocalizedNameOfTheBadge()
    {
        foreach (var number in Enumerable.Range(1, 24))
        {
            var severityClass = AppTelemetryMarkdownExport.SeverityClass(number)!;
            Assert.Equal(
                AppTelemetryMarkdownExport.SeverityLabel(new AppLogEntryDto { SeverityNumber = number }, Bracket),
                AppTelemetryMarkdownExport.SeverityClassLabel(severityClass, Bracket));
        }
        Assert.Null(AppTelemetryMarkdownExport.SeverityClass(0));
    }

    [Theory]
    [InlineData("en", "Trace", "Debug", "Information", "Warning", "Error", "Fatal")]
    [InlineData("fr-FR", "Trace", "Débogage", "Information", "Avertissement", "Erreur", "Fatal")]
    public void R2012_TheClassNames_AreTranslated(string culture, params string[] expected)
    {
        var resources = new System.Resources.ResourceManager("Aetheus.Front.Resources.AppStrings", typeof(Aetheus.Front.Resources.AppStrings).Assembly);
        var cultureInfo = System.Globalization.CultureInfo.GetCultureInfo(culture);

        var names = new[] { "TRACE", "DEBUG", "INFO", "WARN", "ERROR", "FATAL" }
            .Select(severityClass => AppTelemetryMarkdownExport.SeverityClassLabel(
                severityClass, key => resources.GetString(key, cultureInfo) ?? key));

        Assert.Equal(expected, names);
    }

    private static string Bracket(string key) => $"[{key}]";

    [Fact]
    public void ErrorMessageExport_KeepsTheTopFrameTheCellShowsUnderTheMessage()
    {
        Assert.Equal("boom (Shop.Orders.Place)", AppTelemetryMarkdownExport.ErrorMessageExport(
            new AppErrorEventDto { Message = "boom", TopFrame = "Shop.Orders.Place" }));
        Assert.Equal("boom", AppTelemetryMarkdownExport.ErrorMessageExport(new AppErrorEventDto { Message = "boom" }));
    }

    [Fact]
    public void IsoUtc_WritesAServerTimeBackInUtc()
    {
        var utc = new DateTime(2026, 9, 26, 12, 0, 0, 123, DateTimeKind.Utc);

        Assert.Equal("2026-09-26T12:00:00.123Z", AppTelemetryMarkdownExport.IsoUtc(utc));
        Assert.Equal("2026-09-26T12:00:00.123Z", AppTelemetryMarkdownExport.IsoUtc(utc.ToLocalTime()));
    }

    [Theory]
    [InlineData("Aetheus", "aetheus")]
    [InlineData("Éditeur de Données", "editeur-de-donnees")]
    [InlineData("  Shop API / v2 ", "shop-api-v2")]
    [InlineData("---", "")]
    [InlineData(null, "")]
    public void R2036_Slug_IsLowercaseAsciiWordsJoinedByHyphens(string? name, string expected) =>
        Assert.Equal(expected, ExportFileNames.Slug(name));

    [Fact]
    public void R2036_Slug_IsCappedWithoutATrailingHyphen()
    {
        var slug = ExportFileNames.Slug(string.Concat(Enumerable.Repeat("abcdefghi ", 10)));

        Assert.True(slug.Length <= ExportFileNames.MaximumSlugLength);
        Assert.False(slug.EndsWith('-'));
    }

    [Fact]
    public void R2036_Names_StartWithTheOwnerThenTheContent_AndFallBackOnTheId()
    {
        Assert.Equal("aetheus-logs", ExportFileNames.Stem("Aetheus", "app-3", "logs"));
        Assert.Equal("app-3-errors", ExportFileNames.Stem(null, "app-3", "errors"));
        Assert.Equal("aetheus-run-2478-findings-2026-10-01.md",
            ExportFileNames.Dated("Aetheus", "project", "run-2478-findings", new DateTime(2026, 10, 1, 8, 40, 0), "md"));
    }
}
