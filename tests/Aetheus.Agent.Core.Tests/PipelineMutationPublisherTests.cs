// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Operations;
using Aetheus.Agent.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Agent.Core.Tests;

/// <summary>Publish reliability.mutation.score from a mutation-testing-elements report and fail
/// when no score can be computed.</summary>
public sealed class PipelineMutationPublisherTests
{
    private static string Report(params string[] statuses) => JsonSerializer.Serialize(new
    {
        schemaVersion = "2",
        thresholds = new { high = 80, low = 60 },
        files = new Dictionary<string, object>
        {
            ["src/A.cs"] = new { language = "cs", source = "", mutants = statuses.Select((status, index) => new { id = index.ToString(System.Globalization.CultureInfo.InvariantCulture), mutatorName = "m", status }) }
        }
    });

    [Fact]
    public void TheScore_IsDetectedOverValid_AsStrykerComputesIt()
    {
        var score = PipelineMutationPublisher.TryReadScore(
            Report("Killed", "Killed", "Timeout", "Survived", "NoCoverage", "CompileError", "Ignored"));

        Assert.NotNull(score);
        Assert.Equal(5, score.Valid);
        Assert.Equal(2, score.Excluded);
        Assert.Equal(60, score.Percent, precision: 6);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"files":{"a.cs":{}}}""")]
    [InlineData("not json")]
    public void ADocumentThatIsNotAReport_IsNotScored(string json)
    {
        Assert.Null(PipelineMutationPublisher.TryReadScore(json));
    }

    [Fact]
    public async Task OneReport_PublishesTheScoreAsAQualityMetric()
    {
        var (result, api, _) = await RunAsync(("mutation-report.json", Report("Killed", "Survived", "Killed", "Killed")));

        Assert.Equal(0, result.ExitCode);
        await api.Received(1).PublishAnalysisReportAsync(42, Arg.Is<PublishAnalysisReportRequest>(request =>
                request.ScannerKey == "stryker-mutation"
                && request.Category == AnalysisCategory.CodeQuality
                && request.Format == AnalysisReportFormat.MetricsJson
                && request.ReportContent.Contains("\"reliability.mutation.score\"", StringComparison.Ordinal)
                && request.ReportContent.Contains("\"value\":75", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AReportWithNoValidMutant_FailsInsteadOfPublishingZero()
    {
        var (result, api, output) = await RunAsync(("mutation-report.json", Report("CompileError", "Ignored")));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(output, line => line.Contains("no valid mutant", StringComparison.Ordinal));
        await api.DidNotReceive().PublishAnalysisReportAsync(Arg.Any<int>(), Arg.Any<PublishAnalysisReportRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoReport_OrTwo_FailTheStep()
    {
        var (none, _, _) = await RunAsync();
        var (two, api, _) = await RunAsync(("a/mutation-report.json", Report("Killed")), ("b/mutation-report.json", Report("Killed")));

        Assert.NotEqual(0, none.ExitCode);
        Assert.NotEqual(0, two.ExitCode);
        await api.DidNotReceive().PublishAnalysisReportAsync(Arg.Any<int>(), Arg.Any<PublishAnalysisReportRequest>(), Arg.Any<CancellationToken>());
    }

    private static async Task<(ExecutorResult Result, IServerApiClient Api, List<string> Output)> RunAsync(
        params (string Path, string Content)[] files)
    {
        var workDir = Directory.CreateTempSubdirectory("prm-mutation-").FullName;
        try
        {
            foreach (var (path, content) in files)
            {
                var full = Path.Combine(workDir, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                await File.WriteAllTextAsync(full, content, TestContext.Current.CancellationToken);
            }
            var api = Substitute.For<IServerApiClient>();
            api.UploadArtifactAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
                .Returns(new PipelineArtifactDto { Id = 7 });
            api.PublishAnalysisReportAsync(Arg.Any<int>(), Arg.Any<PublishAnalysisReportRequest>(), Arg.Any<CancellationToken>())
                .Returns(new AnalysisReportDto { GateStatus = AnalysisGateStatus.Passed });
            var output = new List<string>();
            var executor = new PipelineArtifactOperationExecutor(
                api, NullLogger<PipelineArtifactOperationExecutor>.Instance, TimeProvider.System);

            var result = await executor.ExecuteAsync(
                OperationKind.PipelinePublishMutation,
                target: "[\"**/mutation-report.json\"]",
                envVars: new Dictionary<string, string> { ["AETHEUS_RUN_ID"] = "42", ["AETHEUS_WORKING_DIR"] = workDir },
                timeoutSeconds: 60,
                onOutput: (line, _) => { output.Add(line); return Task.CompletedTask; },
                cancellationToken: TestContext.Current.CancellationToken);
            return (result, api, output);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }
}
