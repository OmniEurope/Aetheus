// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Configuration;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.Components.AppBackups;

/// <summary>
/// ADR-024 4.3: fires scheduled backups and periodic restore-checks. Every minute it evaluates each
/// enabled policy's <c>ScheduleCron</c> (dispatch a backup) and <c>RestoreCheckCron</c> (dispatch a
/// restore-check of the latest successful run). Structured like <c>PipelineSchedulerService</c>; due-calc
/// via <see cref="BackupSchedule"/>.
/// </summary>
public sealed class BackupSchedulerService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<BackupSchedulerService> logger,
    IPostgresLeaderLease? leaderLease = null)
    : PeriodicBackgroundService(
        BackendRuntimeDefaults.SchedulerCheckInterval, leaderLease, "aetheus:backup-scheduler", timeProvider)
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(2);

    protected override Task ExecuteIterationAsync(CancellationToken ct) => TickAsync(ct);

    protected override void LogIterationError(Exception exception) =>
        logger.LogWarning(exception, "Backup scheduler tick failed; will retry next interval");
    // internal (not private) so a unit test can drive one tick deterministically without a PeriodicTimer
    // (BackgroundServiceTestabilityAuditTests). See PipelineSchedulerService / HeartbeatService.
    internal async Task TickAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IBackupRepository>();
        var service = scope.ServiceProvider.GetRequiredService<IBackupPolicyService>();
        var now = Clock.GetUtcNow().UtcDateTime;

        foreach (var policy in await repo.GetEnabledPoliciesAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await ProcessPolicyAsync(repo, service, policy, now, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One broken policy must not stop the other policies' backups.
                logger.LogWarning(ex, "Backup scheduler failed for policy {PolicyId}; the other policies continue", policy.Id);
            }
        }
    }

    private static async Task ProcessPolicyAsync(
        IBackupRepository repo, IBackupPolicyService service, BackupPolicy policy, DateTime now, CancellationToken ct)
    {
        if (BackupSchedule.IsDue(policy.ScheduleCron, now, policy.LastRunAt, Window))
            await service.TriggerBackupAsync(policy, ct).ConfigureAwait(false);

        if (BackupSchedule.IsDue(policy.RestoreCheckCron, now, policy.LastRestoreCheckAt, Window))
        {
            var latest = await repo.GetLatestSuccessfulRunAsync(policy.Id, ct).ConfigureAwait(false);
            if (latest is not null)
                await service.TriggerRestoreCheckAsync(latest, policy, ct).ConfigureAwait(false);
        }
    }
}
