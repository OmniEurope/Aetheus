// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Aetheus.Back.Tests.Analysis;

/// <summary>A finished run's result is computed once per stamp and reused; any change of the stamp
/// (a decision, a later scan, a report) recomputes it; a run still going is never cached;
/// Dependency-Track is applied on every read, outside the cache.</summary>
public sealed class AnalysisRunResultServiceTests : IDisposable
{
    private static readonly DateTime Seen = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private readonly IAnalysisRepository _repository = Substitute.For<IAnalysisRepository>();
    private readonly IAnalysisRunResultRepository _results = Substitute.For<IAnalysisRunResultRepository>();
    private readonly AnalysisRunResultCache _cache = new();

    private AnalysisRunResultService Service(DependencyTrackOptions? options = null) =>
        new(_repository, _results, _cache, Options.Create(options ?? new DependencyTrackOptions()));

    private static AnalysisRunResultStamp Stamp(bool finished = true, DateTime? updatedAt = null) =>
        new(3, finished, Seen, updatedAt ?? Seen, 40, Seen);

    [Fact]
    public async Task R485_AFinishedRun_IsComputedOncePerStamp_AndRecomputedWhenTheStampMoves()
    {
        var ct = TestContext.Current.CancellationToken;
        _results.GetResultStampAsync(12, ct).Returns(Stamp());
        _repository.GetRunResultSummaryAsync(12, AnalysisRunGateDto.SummaryFindingLimit, ct).Returns(new AnalysisRunGateDto { PipelineRunId = 12, FindingCount = 1 });
        var service = Service();

        Assert.Equal(1, (await service.GetRunResultAsync(12, ct)).FindingCount);
        Assert.Equal(1, (await service.GetRunResultAsync(12, ct)).FindingCount);
        await _repository.Received(1).GetRunResultSummaryAsync(12, AnalysisRunGateDto.SummaryFindingLimit, ct);

        // A decision moves the project's latest finding change: the stale result is not served.
        _results.GetResultStampAsync(12, ct).Returns(Stamp(updatedAt: Seen.AddMinutes(1)));
        _repository.GetRunResultSummaryAsync(12, AnalysisRunGateDto.SummaryFindingLimit, ct).Returns(new AnalysisRunGateDto { PipelineRunId = 12, FindingCount = 0, DecidedFindingCount = 1 });
        Assert.Equal(0, (await service.GetRunResultAsync(12, ct)).FindingCount);
        await _repository.Received(2).GetRunResultSummaryAsync(12, AnalysisRunGateDto.SummaryFindingLimit, ct);
    }

    [Fact]
    public async Task R485_ARunStillGoing_IsNeverCached()
    {
        var ct = TestContext.Current.CancellationToken;
        _results.GetResultStampAsync(12, ct).Returns(Stamp(finished: false));
        _repository.GetRunResultSummaryAsync(12, AnalysisRunGateDto.SummaryFindingLimit, ct).Returns(new AnalysisRunGateDto { PipelineRunId = 12 });
        var service = Service();

        await service.GetRunResultAsync(12, ct);
        await service.GetRunResultAsync(12, ct);

        await _repository.Received(2).GetRunResultSummaryAsync(12, AnalysisRunGateDto.SummaryFindingLimit, ct);
    }

    [Fact]
    public async Task R485_AnUnknownRun_IsNotFound_WithoutComputingAnything()
    {
        var ct = TestContext.Current.CancellationToken;
        _results.GetResultStampAsync(12, ct).Returns((AnalysisRunResultStamp?)null);

        await Assert.ThrowsAsync<NotFoundException>(() => Service().GetRunResultAsync(12, ct));
        await _repository.DidNotReceiveWithAnyArgs().GetRunResultSummaryAsync(default, default, ct);
    }

    [Fact]
    public async Task R485_DependencyTrack_IsReadOnEveryRequest_NotFrozenInTheCache()
    {
        var ct = TestContext.Current.CancellationToken;
        _results.GetResultStampAsync(12, ct).Returns(Stamp());
        _repository.GetRunResultSummaryAsync(12, AnalysisRunGateDto.SummaryFindingLimit, ct).Returns(new AnalysisRunGateDto { PipelineRunId = 12, Status = AnalysisGateStatus.Passed });
        _repository.GetDependencyTrackGateStateAsync(12, ct).Returns(
            new DependencyTrackGateState(true, [DependencyTrackOutboxStatuses.Pending]),
            new DependencyTrackGateState(true, [DependencyTrackOutboxStatuses.Succeeded]));
        var service = Service(new DependencyTrackOptions { Enabled = true, Required = true });

        var pending = await service.GetRunResultAsync(12, ct);
        var settled = await service.GetRunResultAsync(12, ct);

        Assert.Equal(AnalysisGateStatus.Error, pending.Status);
        Assert.Contains("dependency-track:pending", pending.MissingProducers);
        Assert.Equal(AnalysisGateStatus.Passed, settled.Status);
        await _repository.Received(1).GetRunResultSummaryAsync(12, AnalysisRunGateDto.SummaryFindingLimit, ct);
    }

    [Fact]
    public async Task R485_FindingsPage_IsBoundedAndDeduplicatesTheRunSet()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = new AnalysisRunFindingsRequest { RunIds = [5, 5, 6], Page = 0, PageSize = 10_000 };

        await Service().GetRunFindingsAsync(request, ct);

        await _results.Received(1).GetFindingsPageAsync(
            Arg.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { 5, 6 })),
            request, 1, Arg.Is<int>(size => size <= PaginationRequest.MaxPageSize), ct);
    }

    public void Dispose() => _cache.Dispose();
}
