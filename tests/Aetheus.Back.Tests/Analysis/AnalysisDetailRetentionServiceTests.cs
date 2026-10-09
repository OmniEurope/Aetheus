// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests.Analysis;

/// <summary>
/// PLAN-013 lots 2 and 4: the analysis detail purge reads each project's « Détail des analyses »
/// duration, deletes in bounded batches, stops a sweep at its batch budget, and never spins on a report
/// another backend already purged. What is selected and deleted on PostgreSQL is proven by
/// <c>AnalysisDetailRetentionIntegrationTests</c>.
/// </summary>
public sealed class AnalysisDetailRetentionServiceTests
{
    private static readonly DateTime Now = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
    private readonly IAnalysisDetailRetentionRepository _repository = Substitute.For<IAnalysisDetailRetentionRepository>();

    public AnalysisDetailRetentionServiceTests() =>
        _repository.GetProjectRetentionsAsync(Arg.Any<CancellationToken>())
            .Returns([new ProjectAnalysisRetention(1, 30)]);

    private AnalysisDetailRetentionService Service() => new(
        _repository, NullLogger<AnalysisDetailRetentionService>.Instance);

    [Fact]
    public async Task EachProject_IsPurgedAfterItsOwnDuration_ProjectsSharingADurationTogether()
    {
        _repository.GetProjectRetentionsAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new ProjectAnalysisRetention(1, 7),
            new ProjectAnalysisRetention(2, 30),
            new ProjectAnalysisRetention(3, 7)
        ]);
        _repository.GetReportsWithExpiredDetailAsync(
                Arg.Any<DateTime>(), Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);

        await Service().PurgeAsync(Now, TestContext.Current.CancellationToken);

        await _repository.Received(1).GetReportsWithExpiredDetailAsync(
            Now.AddDays(-7), Arg.Is<IReadOnlyCollection<int>>(ids => ids.Order().SequenceEqual(new[] { 1, 3 })),
            AnalysisDetailRetentionService.ReportsPerRound, Arg.Any<CancellationToken>());
        await _repository.Received(1).GetReportsWithExpiredDetailAsync(
            Now.AddDays(-30), Arg.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { 2 })),
            AnalysisDetailRetentionService.ReportsPerRound, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ASweep_DeletesBatchByBatch_ThenComponents_AndStopsWhenNothingIsLeft()
    {
        _repository.GetReportsWithExpiredDetailAsync(
                Arg.Any<DateTime>(), Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<int> { 1, 2 }, new List<int>());
        _repository.DeleteMetricDetailBatchAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(5_000, 1_200, 0);
        _repository.DeleteComponentBatchAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(40, 0);

        var result = await Service().PurgeAsync(Now, TestContext.Current.CancellationToken);

        Assert.Equal((2, 6_200L, 40L, 3), (result.Reports, result.Metrics, result.Components, result.Batches));
        await _repository.Received(3).DeleteMetricDetailBatchAsync(
            Arg.Any<IReadOnlyCollection<int>>(), AnalysisDetailRetentionService.BatchSize, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ASweep_StopsAtItsBatchBudget_TheNextSweepContinues()
    {
        _repository.GetReportsWithExpiredDetailAsync(
                Arg.Any<DateTime>(), Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([1]);
        _repository.DeleteMetricDetailBatchAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(AnalysisDetailRetentionService.BatchSize);

        var result = await Service().PurgeAsync(Now, TestContext.Current.CancellationToken);

        Assert.Equal(AnalysisDetailRetentionService.MaxBatchesPerSweep, result.Batches);
        Assert.Equal((long)AnalysisDetailRetentionService.MaxBatchesPerSweep * AnalysisDetailRetentionService.BatchSize, result.Metrics);
        await _repository.DidNotReceive().DeleteComponentBatchAsync(
            Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ARoundThatDeletesNothing_EndsTheSweep_InsteadOfSpinning()
    {
        _repository.GetReportsWithExpiredDetailAsync(
                Arg.Any<DateTime>(), Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([1]);
        _repository.DeleteMetricDetailBatchAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(0);
        _repository.DeleteComponentBatchAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(0);

        var result = await Service().PurgeAsync(Now, TestContext.Current.CancellationToken);

        Assert.Equal((1, 0), (result.Reports, result.Batches));
        await _repository.Received(1).GetReportsWithExpiredDetailAsync(
            Arg.Any<DateTime>(), Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }
}
