// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Agent.Core.Operations;
using Aetheus.Shared.Components.Analysis;
using Aetheus.Shared.Components.Pipelines;

namespace Aetheus.Agent.Core.Tests;

/// <summary>The structure check of a scanner report: the root each JSON format declares, and a whole,
/// DTD-free, bounded XML document for CycloneDX XML, on real files.</summary>
public sealed class ScannerReportStructureTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"scanner-report-{Guid.NewGuid():N}");

    public void Dispose() => File.Delete(_path);

    private async Task<bool> ValidateAsync(string format, string content, long maxBytes = 1024)
    {
        await File.WriteAllTextAsync(_path, content, TestContext.Current.CancellationToken);
        return await ScannerOperationValidator.ValidateReportStructureAsync(
            new ScannerManifestEntry { ReportFormat = format, MaxReportBytes = maxBytes }, _path, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(AnalysisReportFormat.Sarif, """{"version":"2.1.0","runs":[]}""", true)]
    [InlineData(AnalysisReportFormat.Sarif, """{"version":"2.1.0","runs":{}}""", false)]
    [InlineData(AnalysisReportFormat.Sarif, """{"runs":[]}""", false)]
    [InlineData(AnalysisReportFormat.Sarif, """[]""", false)]
    [InlineData(AnalysisReportFormat.CycloneDxJson, """{"bomFormat":"cyclonedx"}""", true)]
    [InlineData(AnalysisReportFormat.CycloneDxJson, """{"components":[]}""", false)]
    [InlineData(AnalysisReportFormat.SpdxJson, """{"spdxVersion":"SPDX-2.3"}""", true)]
    [InlineData(AnalysisReportFormat.SpdxJson, """{"name":"x"}""", false)]
    [InlineData(AnalysisReportFormat.MetricsJson, """[1,2]""", true)]
    [InlineData(AnalysisReportFormat.NativeJson, """{"findings":[]}""", true)]
    [InlineData(AnalysisReportFormat.NativeJson, "\"text\"", false)]
    [InlineData(AnalysisReportFormat.JUnit, """{}""", false)]
    public void HasReportShape_RequiresTheRootOfTheFormat(AnalysisReportFormat format, string json, bool expected)
    {
        using var document = JsonDocument.Parse(json);

        Assert.Equal(expected, ScannerOperationValidator.HasReportShape(format, document.RootElement));
    }

    [Fact]
    public async Task AWellFormedSarifFile_IsAccepted() =>
        Assert.True(await ValidateAsync("sarif", """{"version":"2.1.0","runs":[]}"""));

    [Fact]
    public async Task AJsonFileWithAComment_IsRefused() =>
        Assert.False(await ValidateAsync("NativeJson", "{ /* note */ }"));

    [Fact]
    public async Task AWellFormedCycloneDxXmlFile_IsAccepted() =>
        Assert.True(await ValidateAsync("CycloneDxXml", "<bom xmlns=\"http://cyclonedx.org/schema/bom/1.5\"><components/></bom>"));

    [Theory]
    [InlineData("<bom><components></bom>")]
    [InlineData("<!DOCTYPE bom [<!ENTITY x \"y\">]><bom>&x;</bom>")]
    public async Task AMalformedOrDtdCarryingXmlFile_IsRefused(string xml) =>
        Assert.False(await ValidateAsync("CycloneDxXml", xml));

    [Fact]
    public async Task AnXmlFileAboveTheReportBound_IsRefused() =>
        Assert.False(await ValidateAsync("CycloneDxXml", $"<bom>{new string('a', 200)}</bom>", maxBytes: 64));

    [Fact]
    public async Task AMissingFile_IsRefused()
    {
        var absent = Path.Combine(Path.GetTempPath(), $"scanner-absent-{Guid.NewGuid():N}");

        Assert.False(await ScannerOperationValidator.ValidateReportStructureAsync(
            new ScannerManifestEntry { ReportFormat = "Sarif" }, absent, TestContext.Current.CancellationToken));
        Assert.False(await ScannerOperationValidator.ValidateReportStructureAsync(
            new ScannerManifestEntry { ReportFormat = "CycloneDxXml" }, absent, TestContext.Current.CancellationToken));
    }
}
