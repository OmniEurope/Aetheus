// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Pipelines;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>Who launched a run and which runs it started.</summary>
public sealed class PipelineRunLineageServiceTests
{
    private static readonly DateTime Started = new(2026, 9, 30, 8, 0, 0, DateTimeKind.Utc);
    private readonly IPipelineRunLineageReader _reader = Substitute.For<IPipelineRunLineageReader>();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 30, 9, 0, 0, TimeSpan.Zero));
    private readonly PipelineRunLineageService _service;

    public PipelineRunLineageServiceTests() => _service = new PipelineRunLineageService(_reader, _audit, _time);

    private void Launch(string username) => _audit
        .GetLogsPagedAsync(1, 1, action: "Triggered", entityType: "PipelineRun", entityId: 7,
            dateFrom: Started - PipelineRunLineageService.LaunchSlack, dateTo: Started + PipelineRunLineageService.LaunchSlack,
            ct: Arg.Any<CancellationToken>())
        .Returns(new PaginatedResult<AuditLogDto> { Items = [new AuditLogDto { Username = username }], TotalCount = 1 });

    [Fact]
    public async Task AFinishedRun_NamesWhoLaunchedIt_AndTheRunsItStartedUntilJustAfterItsEnd()
    {
        var ct = TestContext.Current.CancellationToken;
        var completed = Started.AddMinutes(20);
        _reader.GetRunWindowAsync(7, ct).Returns(new PipelineRunWindow(Started, completed));
        var child = new PipelineRunLinkDto { RunId = 9, PipelineName = "deploy-prod", BuildNumber = 3 };
        _reader.GetDownstreamRunsAsync(7, Started, completed + PipelineRunLineageService.LaunchSlack, ct).Returns([child]);
        Launch("sony");

        var lineage = await _service.GetLineageAsync(7, ct);

        Assert.NotNull(lineage);
        Assert.Equal("sony", lineage.TriggeredBy);
        Assert.Equal([child], lineage.Downstream);
    }

    [Fact]
    public async Task ARunNobodyLaunched_HasNoPerson_AndARunningOneIsReadUpToNow()
    {
        var ct = TestContext.Current.CancellationToken;
        _reader.GetRunWindowAsync(7, ct).Returns(new PipelineRunWindow(Started, null));
        _reader.GetDownstreamRunsAsync(7, Started, _time.GetUtcNow().UtcDateTime + PipelineRunLineageService.LaunchSlack, ct).Returns([]);
        Launch(PipelineRunLineageService.SystemActor);

        var lineage = await _service.GetLineageAsync(7, ct);

        Assert.NotNull(lineage);
        Assert.Null(lineage.TriggeredBy);
        Assert.Empty(lineage.Downstream);
        await _reader.Received(1).GetDownstreamRunsAsync(
            7, Started, _time.GetUtcNow().UtcDateTime + PipelineRunLineageService.LaunchSlack, ct);
    }

    [Fact]
    public async Task AnUnknownRun_HasNoLineage()
    {
        var ct = TestContext.Current.CancellationToken;
        _reader.GetRunWindowAsync(404, ct).Returns((PipelineRunWindow?)null);

        Assert.Null(await _service.GetLineageAsync(404, ct));
    }
}
