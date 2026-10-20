// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Releases;
using Aetheus.Back.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.Releases;

/// <summary>Recette R1-72: the release retention sweep, its window, its batches and its audit entry.</summary>
public sealed class ReleaseRetentionServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private static (ReleaseRetentionService Sut, IReleaseRetentionRepository Repository, IAuditService Audit) BuildSut(
        Dictionary<string, string?>? settings = null)
    {
        var repository = Substitute.For<IReleaseRetentionRepository>();
        var audit = Substitute.For<IAuditService>();
        var scopeFactory = new ServiceCollection()
            .AddScoped(_ => repository)
            .AddScoped(_ => audit)
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings ?? []).Build();
        var sut = new ReleaseRetentionService(
            scopeFactory, configuration, new FakeTimeProvider(Now), NullLogger<ReleaseRetentionService>.Instance,
            Substitute.For<IPostgresLeaderLease>());
        return (sut, repository, audit);
    }

    [Fact]
    public async Task TheDefaultWindow_IsThirtyDays()
    {
        var (sut, repository, _) = BuildSut();
        repository.GetExpiredReleaseIdsAsync(Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);

        await sut.ArchiveExpiredReleasesAsync(TestContext.Current.CancellationToken);

        await repository.Received(1).GetExpiredReleaseIdsAsync(
            Now.UtcDateTime.AddDays(-30), Now.UtcDateTime, ReleaseRetentionService.BatchSize, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheWindow_ComesFromTheSettings_Clamped()
    {
        var (sut, repository, _) = BuildSut(new() { ["Retention:ReleaseDays"] = "0" });
        repository.GetExpiredReleaseIdsAsync(Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);

        await sut.ArchiveExpiredReleasesAsync(TestContext.Current.CancellationToken);

        await repository.Received(1).GetExpiredReleaseIdsAsync(
            Now.UtcDateTime.AddDays(-1), Now.UtcDateTime, ReleaseRetentionService.BatchSize, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ASweep_ArchivesBatchByBatch_ThenWritesOneAuditEntry()
    {
        var (sut, repository, audit) = BuildSut();
        repository.GetExpiredReleaseIdsAsync(Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([1, 2], [3], []);
        repository.ArchiveExpiredAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(2, 1);

        var archived = await sut.ArchiveExpiredReleasesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, archived);
        await repository.Received(2).ArchiveExpiredAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        await audit.Received(1).LogAsync(
            "ReleaseRetentionArchived", "Release", null,
            Arg.Is<string>(details => details.StartsWith("archived=3; retention=30 d;", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ABatchWhoseCandidatesAllBecameProtected_EndsTheSweep_WithoutAudit()
    {
        var (sut, repository, audit) = BuildSut();
        repository.GetExpiredReleaseIdsAsync(Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([1]);
        repository.ArchiveExpiredAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(0);

        Assert.Equal(0, await sut.ArchiveExpiredReleasesAsync(TestContext.Current.CancellationToken));

        await repository.Received(1).ArchiveExpiredAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        await audit.DidNotReceive().LogAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ASweep_StopsAfterItsBatchBudget()
    {
        var (sut, repository, _) = BuildSut();
        repository.GetExpiredReleaseIdsAsync(Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([1]);
        repository.ArchiveExpiredAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(1);

        Assert.Equal(ReleaseRetentionService.MaxBatchesPerSweep,
            await sut.ArchiveExpiredReleasesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheSweep_RunsUnderItsOwnLeaderLease()
    {
        var lease = Substitute.For<IPostgresLeaderLease>();
        var invoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lease.RunAsLeaderAsync(Arg.Any<string>(), Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                invoked.TrySetResult();
                return Task.CompletedTask;
            });
        var service = new ReleaseRetentionService(
            Substitute.For<IServiceScopeFactory>(), new ConfigurationBuilder().Build(), TimeProvider.System,
            NullLogger<ReleaseRetentionService>.Instance, lease);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await invoked.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await lease.Received(1).RunAsLeaderAsync(
            "aetheus:release-retention", Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>());
    }
}
