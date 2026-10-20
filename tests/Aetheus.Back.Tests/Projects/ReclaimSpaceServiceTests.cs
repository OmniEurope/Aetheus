// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Projects;
using Aetheus.Back.Exceptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests.Projects;

/// <summary> « Récupérer la place » runs one purged table at a time, never while a pipeline runs,
/// measured before and after and written to the audit log at its start and its end. </summary>
public sealed class ReclaimSpaceServiceTests
{
    private readonly IReclaimSpaceRepository _repository = Substitute.For<IReclaimSpaceRepository>();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly ReclaimSpaceTracker _tracker = new();
    private readonly ServiceProvider _provider;

    public ReclaimSpaceServiceTests()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _repository);
        services.AddScoped(_ => _audit);
        _provider = services.BuildServiceProvider();
    }

    private ReclaimSpaceService Service() => new(
        _repository, _tracker, _provider.GetRequiredService<IServiceScopeFactory>(), _audit, TimeProvider.System,
        NullLogger<ReclaimSpaceService>.Instance);

    [Fact]
    public async Task ATableTheRetentionDoesNotPurge_IsRefused()
    {
        await Assert.ThrowsAsync<BadRequestException>(() =>
            Service().StartAsync("Users", "admin", TestContext.Current.CancellationToken));
        await _repository.DidNotReceive().VacuumFullAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhileAPipelineRuns_TheReclaimIsRefused_AndTheSlotIsFree()
    {
        _repository.CountActiveRunsAsync(Arg.Any<CancellationToken>()).Returns(2);

        await Assert.ThrowsAsync<ConflictException>(() =>
            Service().StartAsync("AnalysisMetrics", "admin", TestContext.Current.CancellationToken));

        Assert.Null(_tracker.RunningTable);
        await _repository.DidNotReceive().VacuumFullAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _audit.DidNotReceive().LogAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ASecondTable_WaitsForTheFirst()
    {
        var release = new TaskCompletionSource();
        _repository.VacuumFullAsync("AnalysisMetrics", Arg.Any<CancellationToken>()).Returns(release.Task);
        await Service().StartAsync("AnalysisMetrics", "admin", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ConflictException>(() =>
            Service().StartAsync("AppMetricSamples", "admin", TestContext.Current.CancellationToken));

        release.SetResult();
        await _tracker.Current;
        Assert.Null(_tracker.RunningTable);
    }

    [Fact]
    public async Task AReclaim_IsMeasuredBeforeAndAfter_AndAuditedAtBothEnds()
    {
        _repository.GetTableBytesAsync("AnalysisMetrics", Arg.Any<CancellationToken>()).Returns(2_500_000_000L, 300_000_000L);

        var started = await Service().StartAsync("AnalysisMetrics", "admin", TestContext.Current.CancellationToken);
        await _tracker.Current;

        Assert.Equal(2_500_000_000L, started.BytesBefore);
        var result = Assert.Single(_tracker.History);
        Assert.True(result.Succeeded);
        Assert.Equal((2_500_000_000L, 300_000_000L), (result.BytesBefore!.Value, result.BytesAfter!.Value));
        Assert.Equal("admin", result.RequestedBy);
        await _repository.Received(1).VacuumFullAsync("AnalysisMetrics", Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync("ReclaimSpaceStarted", "Database", null,
            Arg.Is<string>(details => details.Contains("before=2500000000", StringComparison.Ordinal)), Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync("ReclaimSpaceCompleted", "Database", null,
            Arg.Is<string>(details => details.Contains("after=300000000", StringComparison.Ordinal)
                && details.Contains("requested by=admin", StringComparison.Ordinal)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AFailedVacuum_IsReportedAndAudited()
    {
        _repository.VacuumFullAsync("AppLogEntries", Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("lock timeout")));

        await Service().StartAsync("AppLogEntries", "admin", TestContext.Current.CancellationToken);
        await _tracker.Current;

        var result = Assert.Single(_tracker.History);
        Assert.False(result.Succeeded);
        Assert.Equal("lock timeout", result.Error);
        await _audit.Received(1).LogAsync("ReclaimSpaceFailed", "Database", null, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }
}
