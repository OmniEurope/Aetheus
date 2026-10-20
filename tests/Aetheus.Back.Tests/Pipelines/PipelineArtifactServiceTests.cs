// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class PipelineArtifactServiceTests
{
    private readonly IPipelineRepository _repoMock = Substitute.For<IPipelineRepository>();
    private readonly PipelineArtifactService _sut;

    public PipelineArtifactServiceTests()
    {
        _sut = new PipelineArtifactService(_repoMock, _repoMock, _repoMock, TimeProvider.System, NullLogger<PipelineArtifactService>.Instance);
    }

    [Fact]
    public async Task GetArtifactsAsync_ReturnsMappedList()
    {
        _repoMock.GetArtifactsAsync(1, Arg.Any<CancellationToken>())
            .Returns([new PipelineArtifact { Id = 1, PipelineRunId = 1, Name = "build.zip", FilePath = "/tmp/build.zip" }]);

        var result = await _sut.GetArtifactsAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal("build.zip", result[0].Name);
    }

    [Fact]
    public async Task PublishArtifactAsync_RunNotFound_ReturnsNull()
    {
        _repoMock.GetPipelineIdForRunAsync(99, Arg.Any<CancellationToken>())
            .Returns((int?)null);

        var result = await _sut.PublishArtifactAsync(99, new PublishArtifactRequest { Name = "a", FilePath = "/a" }, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task PublishArtifactAsync_ValidRun_CreatesAndReturnsArtifact()
    {
        _repoMock.GetPipelineIdForRunAsync(1, Arg.Any<CancellationToken>())
            .Returns((int?)1);
        _repoMock.AddArtifactAsync(Arg.Any<PipelineArtifact>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var result = await _sut.PublishArtifactAsync(1, new PublishArtifactRequest
        {
            Name = "output.zip",
            FilePath = "/tmp/output.zip",
            StageName = "build",
            StepName = "compile"
        }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("output.zip", result.Name);
        await _repoMock.Received(1).AddArtifactAsync(Arg.Any<PipelineArtifact>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetTestResultsAsync_ReturnsMappedList()
    {
        _repoMock.GetTestResultsAsync(1, Arg.Any<CancellationToken>())
            .Returns([new TestResult
            {
                Id = 1, PipelineRunId = 1, TestName = "Test1", TestSuite = "Suite1",
                Outcome = TestOutcome.Passed, DurationMs = 100
            }]);

        var result = await _sut.GetTestResultsAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal("Test1", result[0].TestName);
        Assert.Equal(TestOutcome.Passed, result[0].Outcome);
    }

    [Fact]
    public async Task PublishTestResultsAsync_RunNotFound_ReturnsNull()
    {
        _repoMock.GetPipelineIdForRunAsync(99, Arg.Any<CancellationToken>())
            .Returns((int?)null);

        var result = await _sut.PublishTestResultsAsync(99, new PublishTestResultsRequest
        {
            XmlContent = "<testsuite />",
            Format = "junit"
        }, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task PublishTestResultsAsync_ValidJUnit_ReturnsSummary()
    {
        var xml = """
            <testsuite tests="3">
                <testcase name="T1" classname="S" time="0.1" />
                <testcase name="T2" classname="S" time="0.2">
                    <failure message="fail" />
                </testcase>
                <testcase name="T3" classname="S" time="0">
                    <skipped />
                </testcase>
            </testsuite>
            """;

        _repoMock.GetPipelineIdForRunAsync(1, Arg.Any<CancellationToken>())
            .Returns((int?)1);
        _repoMock.AddTestResultsAsync(Arg.Any<List<TestResult>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var result = await _sut.PublishTestResultsAsync(1, new PublishTestResultsRequest
        {
            XmlContent = xml,
            Format = "junit",
            StageName = "test",
            StepName = "run-tests"
        }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(3, result.TotalTests);
        Assert.Equal(1, result.Passed);
        Assert.Equal(1, result.Failed);
        Assert.Equal(1, result.Skipped);
    }

    // --- PublishComplexityAsync (L) - #6 ---

    [Fact]
    public async Task PublishComplexityAsync_RunNotFound_ReturnsFalse()
    {
        _repoMock.GetPipelineIdForRunAsync(99, Arg.Any<CancellationToken>())
            .Returns((int?)null);

        var ok = await _sut.PublishComplexityAsync(99, new PublishComplexityRequest { AvgCyclomatic = 4 }, ct: TestContext.Current.CancellationToken);

        Assert.False(ok);
        await _repoMock.DidNotReceive().AddRunMetricsAsync(Arg.Any<IEnumerable<RunMetric>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishComplexityAsync_NoCoverage_PublishesBaseMetricsWithoutCrap()
    {
        _repoMock.GetPipelineIdForRunAsync(1, Arg.Any<CancellationToken>())
            .Returns((int?)1);
        _repoMock.GetCoverageResultsAsync(1, Arg.Any<CancellationToken>()).Returns([]);
        List<RunMetric>? captured = null;
        await _repoMock.AddRunMetricsAsync(Arg.Do<IEnumerable<RunMetric>>(m => captured = m.ToList()), Arg.Any<CancellationToken>());

        var ok = await _sut.PublishComplexityAsync(1, new PublishComplexityRequest
        {
            AvgCyclomatic = 4,
            MaxCyclomatic = 12,
            TotalMethods = 30,
            HighComplexityMethods = 2,
            TotalLinesOfCode = 500,
            StageName = "lint"
        }, ct: TestContext.Current.CancellationToken);

        Assert.True(ok);
        Assert.NotNull(captured);
        var keys = captured!.Select(m => m.Key).ToList();
        Assert.Equal(new[] { "loc.total", "complexity.methods", "complexity.cyclomatic.avg", "complexity.cyclomatic.max", "complexity.high" }, keys);
        Assert.DoesNotContain("complexity.crap.avg", keys); // no coverage → CRAP omitted, not faked
        Assert.All(captured!, m => Assert.Equal("lint", m.StageName));
    }

    [Fact]
    public async Task PublishComplexityAsync_WithCoverage_DerivesCrapFromMostComprehensiveReport()
    {
        _repoMock.GetPipelineIdForRunAsync(1, Arg.Any<CancellationToken>())
            .Returns((int?)1);
        _repoMock.GetCoverageResultsAsync(1, Arg.Any<CancellationToken>()).Returns(
        [
            new CoverageResult { PipelineRunId = 1, LineRate = 0.20, LinesValid = 10 },
            new CoverageResult { PipelineRunId = 1, LineRate = 0.75, LinesValid = 100 }
        ]);
        List<RunMetric>? captured = null;
        await _repoMock.AddRunMetricsAsync(Arg.Do<IEnumerable<RunMetric>>(m => captured = m.ToList()), Arg.Any<CancellationToken>());

        var ok = await _sut.PublishComplexityAsync(1, new PublishComplexityRequest { AvgCyclomatic = 4, StageName = "lint" }, ct: TestContext.Current.CancellationToken);

        Assert.True(ok);
        var crap = Assert.Single(captured!, m => m.Key == "complexity.crap.avg");
        // CRAP = cc^2 * (1 - coverage)^3 + cc = 16 * 0.25^3 + 4 = 0.25 + 4 = 4.25.
        Assert.Equal(4.25, crap.Value);
    }
}
