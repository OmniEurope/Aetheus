// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>ADR-060: what the worker decides between two batches, on a substitute repository. The batches
/// themselves ran on PostgreSQL in a migration test removed once the migration shipped (docs/audit/tests-integration-e2e/migrations-livrees.md).</summary>
public sealed class PipelineRunIdentityBackfillServiceTests
{
    private readonly IPipelineRunIdentityBackfillRepository _repository = Substitute.For<IPipelineRunIdentityBackfillRepository>();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));

    private PipelineRunIdentityBackfillService NewWorker(IPostgresLeaderLease? lease = null)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _repository);
        return new PipelineRunIdentityBackfillService(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PipelineRunIdentityBackfillService>.Instance,
            _clock,
            lease ?? LeaderLeaseDouble.Leader());
    }

    private void Batches(params int?[] highest)
    {
        var next = 0;
        _repository.FillNextBatchAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => next < highest.Length ? highest[next++] : null);
    }

    [Fact]
    public async Task EachBatch_ContinuesAfterTheLastRowOfThePreviousOne_ByThousand()
    {
        var ct = TestContext.Current.CancellationToken;
        Batches(1000, 2400, null);
        var worker = NewWorker();

        await worker.FillNextBatchAsync(ct);
        await worker.FillNextBatchAsync(ct);
        await worker.FillNextBatchAsync(ct);

        Received.InOrder(() =>
        {
            _repository.FillNextBatchAsync(0, 1000, ct);
            _repository.FillNextBatchAsync(1000, 1000, ct);
            _repository.FillNextBatchAsync(2400, 1000, ct);
        });
    }

    [Fact]
    public async Task ASweepThatFoundNothing_WaitsBeforeLookingAgain_FromTheFirstRow()
    {
        var ct = TestContext.Current.CancellationToken;
        Batches(1000, null);
        var worker = NewWorker();
        await worker.FillNextBatchAsync(ct);
        await worker.FillNextBatchAsync(ct);
        _repository.ClearReceivedCalls();

        _clock.Advance(PipelineRunIdentityBackfillService.RecheckInterval - TimeSpan.FromSeconds(1));
        await worker.FillNextBatchAsync(ct);
        await _repository.DidNotReceive().FillNextBatchAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());

        _clock.Advance(TimeSpan.FromSeconds(1));
        await worker.FillNextBatchAsync(ct);
        await _repository.Received(1).FillNextBatchAsync(0, 1000, ct);
    }

    [Fact]
    public async Task AFailedBatch_IsRetriedFromTheSameRow()
    {
        var ct = TestContext.Current.CancellationToken;
        _repository.FillNextBatchAsync(0, 1000, ct).Returns(500);
        _repository.FillNextBatchAsync(500, 1000, ct).Returns(
            _ => throw new InvalidOperationException("database down"),
            _ => (int?)900);
        var worker = NewWorker();
        await worker.FillNextBatchAsync(ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => worker.FillNextBatchAsync(ct));
        await worker.FillNextBatchAsync(ct);

        await _repository.Received(2).FillNextBatchAsync(500, 1000, ct);
    }

    [Fact]
    public async Task TheWorker_RunsUnderItsOwnLeaderLease()
    {
        var ct = TestContext.Current.CancellationToken;
        var lease = Substitute.For<IPostgresLeaderLease>();
        var invoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lease.RunAsLeaderAsync(Arg.Any<string>(), Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                invoked.TrySetResult();
                return Task.CompletedTask;
            });

        await NewWorker(lease).StartAsync(ct);
        await invoked.Task.WaitAsync(ct);

        await lease.Received(1).RunAsLeaderAsync(
            "aetheus:pipeline-run-identity-backfill", Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void TheModule_RegistersTheWorkerAndItsRepository()
    {
        var services = new ServiceCollection();

        services.AddPipelinesModule();

        Assert.Contains(services, service => service.ImplementationType == typeof(PipelineRunIdentityBackfillService));
        Assert.Contains(services, service => service.ServiceType == typeof(IPipelineRunIdentityBackfillRepository));
    }
}
