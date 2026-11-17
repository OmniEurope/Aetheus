// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Services;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// ADR-060, step 2: fills the runs' <c>Uid</c> in the leader instance, one batch per tick, oldest first.
/// Interrupting it loses nothing: the rows left are the ones still empty. Once a sweep finds none it only
/// looks again now and then, for a row the previous version inserted late.
/// </summary>
public sealed class PipelineRunIdentityBackfillService(
    IServiceScopeFactory scopeFactory,
    ILogger<PipelineRunIdentityBackfillService> logger,
    TimeProvider timeProvider,
    IPostgresLeaderLease leaderLease)
    : PeriodicBackgroundService(BatchInterval, leaderLease, "aetheus:pipeline-run-identity-backfill", timeProvider)
{
    internal const int BatchSize = 1000;
    internal static readonly TimeSpan BatchInterval = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan RecheckInterval = TimeSpan.FromHours(1);

    // In memory: a restart starts over from the first row and skips the filled ones.
    private int _afterId;
    private DateTimeOffset _nextSweep;

    protected override Task ExecuteIterationAsync(CancellationToken ct) => FillNextBatchAsync(ct);

    protected override void LogIterationError(Exception exception) =>
        logger.LogError(exception, "Run identity backfill batch failed after run {RunId}", _afterId);

    internal async Task FillNextBatchAsync(CancellationToken ct)
    {
        if (Clock.GetUtcNow() < _nextSweep) return;

        await using var scope = scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPipelineRunIdentityBackfillRepository>();
        if (await repository.FillNextBatchAsync(_afterId, BatchSize, ct).ConfigureAwait(false) is { } highest)
        {
            _afterId = highest;
            return;
        }

        if (_afterId > 0)
            logger.LogInformation("Run identity backfill reached the last run after {RunId}", _afterId);
        _afterId = 0;
        _nextSweep = Clock.GetUtcNow() + RecheckInterval;
    }
}
