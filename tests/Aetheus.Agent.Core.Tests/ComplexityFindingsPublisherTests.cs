// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Agent.Core.Operations;
using Aetheus.Agent.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Agent.Core.Tests;

/// <summary>R1-36 / ADR-056: every method over the complexity threshold is a finding of its own.</summary>
public sealed class ComplexityFindingsPublisherTests
{
    private static string Root => Path.Combine(Path.GetTempPath(), "repo");

    private static ComplexityMethod Method(string relative, int line, string symbol, int cyclomatic) =>
        new(Path.Combine(Root, relative), line, symbol, cyclomatic);

    private static JsonElement Results(string sarif) =>
        JsonDocument.Parse(sarif).RootElement.GetProperty("runs")[0].GetProperty("results");

    [Fact]
    public void BuildSarif_ANonConformingMethodIsOneResult_IdentifiedByItsFileAndSymbol()
    {
        var sarif = ComplexityFindingsPublisher.BuildSarif(Root,
            [Method(Path.Combine("src", "Shop", "Checkout.cs"), 12, "Shop.Checkout.Total(int)", 14)]);

        var result = Assert.Single(Results(sarif).EnumerateArray());
        Assert.Equal("complexity-method", result.GetProperty("ruleId").GetString());
        Assert.Equal("Medium", result.GetProperty("properties").GetProperty("severity").GetString());
        Assert.Equal("warning", result.GetProperty("level").GetString());
        Assert.Contains("Shop.Checkout.Total(int)", result.GetProperty("message").GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Contains("14", result.GetProperty("message").GetProperty("text").GetString(), StringComparison.Ordinal);
        var location = result.GetProperty("locations")[0];
        Assert.Equal("src/Shop/Checkout.cs", location.GetProperty("physicalLocation").GetProperty("artifactLocation").GetProperty("uri").GetString());
        Assert.Equal(12, location.GetProperty("physicalLocation").GetProperty("region").GetProperty("startLine").GetInt32());
        Assert.Equal("Shop.Checkout.Total(int)", location.GetProperty("logicalLocations")[0].GetProperty("fullyQualifiedName").GetString());
    }

    [Fact]
    public void BuildSarif_TheIdentityDoesNotMoveWithTheLineOrTheComplexity()
    {
        var file = Path.Combine("src", "Shop", "Checkout.cs");

        var before = Results(ComplexityFindingsPublisher.BuildSarif(Root, [Method(file, 12, "Shop.Checkout.Total(int)", 12)]))[0];
        var after = Results(ComplexityFindingsPublisher.BuildSarif(Root, [Method(file, 80, "Shop.Checkout.Total(int)", 19)]))[0];
        var other = Results(ComplexityFindingsPublisher.BuildSarif(Root, [Method(file, 12, "Shop.Checkout.Pay(int)", 12)]))[0];

        string Identity(JsonElement result) => result.GetProperty("partialFingerprints").GetProperty("aetheus/identity").GetString()!;
        Assert.Equal(Identity(before), Identity(after));
        Assert.NotEqual(Identity(before), Identity(other));
        Assert.NotEqual(
            before.GetProperty("message").GetProperty("text").GetString(),
            after.GetProperty("message").GetProperty("text").GetString());
    }

    [Fact]
    public void BuildSarif_NoMethodOverTheThreshold_IsAnEmptyRun()
    {
        Assert.Equal(0, Results(ComplexityFindingsPublisher.BuildSarif(Root, [])).GetArrayLength());
    }

    [Fact]
    public async Task PublishComplexity_PublishesTheMethodsOverTheThresholdAsAFailedFindingsReport()
    {
        var workDir = Directory.CreateTempSubdirectory("prm-cc-findings-").FullName;
        try
        {
            var branches = string.Concat(Enumerable.Range(0, 15).Select(index => $"if (n == {index}) return {index}; "));
            await File.WriteAllTextAsync(
                Path.Combine(workDir, "Sample.cs"),
                $"namespace Shop; public class Sample {{ public int Heavy(int n) {{ {branches} return n; }} public int Light() => 1; }}",
                TestContext.Current.CancellationToken);
            var api = Substitute.For<IServerApiClient>();
            api.UploadArtifactAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
                .Returns(new PipelineArtifactDto { Id = 101 });
            api.PublishAnalysisReportAsync(Arg.Any<int>(), Arg.Any<PublishAnalysisReportRequest>(), Arg.Any<CancellationToken>())
                .Returns(new AnalysisReportDto { GateStatus = AnalysisGateStatus.Passed });
            var published = new List<PublishAnalysisReportRequest>();
            api.When(item => item.PublishAnalysisReportAsync(Arg.Any<int>(), Arg.Any<PublishAnalysisReportRequest>(), Arg.Any<CancellationToken>()))
                .Do(call => published.Add(call.ArgAt<PublishAnalysisReportRequest>(1)));

            var result = await PipelineQualityPublisher.PublishComplexityAsync(
                api, NullLogger.Instance, TimeProvider.System,
                new Dictionary<string, string> { ["AETHEUS_RUN_ID"] = "42", ["AETHEUS_WORKING_DIR"] = workDir },
                (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            var report = Assert.Single(published, item => item.ScannerKey == ComplexityFindingsPublisher.ScannerKey);
            Assert.Equal(AnalysisReportStatus.Failed, report.Status);
            Assert.Equal(AnalysisReportFormat.Sarif, report.Format);
            Assert.Equal(AnalysisCategory.CodeQuality, report.Category);
            var finding = Assert.Single(Results(report.ReportContent!).EnumerateArray());
            Assert.Contains("Shop.Sample.Heavy(int)", finding.GetProperty("message").GetProperty("text").GetString(), StringComparison.Ordinal);
            // The aggregate metrics are still published beside it.
            Assert.Contains(published, item => item.ScannerKey == "roslyn-metrics");
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }
}
