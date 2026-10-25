// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Services;
using Microsoft.Extensions.Options;

namespace Aetheus.Back.Components.Git;

public sealed class GitLightMaintenanceService(
    IServiceScopeFactory scopeFactory,
    IOptions<GitLightOptions> options,
    ILogger<GitLightMaintenanceService> logger,
    IPostgresLeaderLease? leaderLease = null) : BackgroundService
{
    // Only the live colour runs it (decision of 2026-10-02, PostgresLeaderLease).
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        leaderLease is null
            ? RunLeaderLoopAsync(stoppingToken)
            : leaderLease.RunAsLeaderAsync("aetheus:gitlight-maintenance", RunLeaderLoopAsync, stoppingToken);

    private async Task RunLeaderLoopAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromHours(options.Value.MaintenanceIntervalHours);
        using var timer = new PeriodicTimer(interval);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await RunMaintenanceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during git maintenance");
            }
        }
    }

    internal async Task RunMaintenanceAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var lightRepo = scope.ServiceProvider.GetRequiredService<IGitLightRepository>();
        var cli = scope.ServiceProvider.GetRequiredService<IGitLightCliService>();
        var lightService = scope.ServiceProvider.GetRequiredService<IGitLightService>();

        var repos = await lightRepo.GetAllAsync(ct).ConfigureAwait(false);
        var defaultBranchesChanged = 0;

        foreach (var repo in repos)
        {
            try
            {
                var diskPath = lightService.ResolveDiskPath(repo.ProjectId, repo.Slug);
                if (!Directory.Exists(diskPath)) continue;

                var detectedDefaultBranch = await cli
                    .DetectDefaultBranchAsync(diskPath, ct)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(detectedDefaultBranch)
                    && !string.Equals(
                        repo.DefaultBranch,
                        detectedDefaultBranch,
                        StringComparison.Ordinal))
                {
                    logger.LogInformation(
                        "Default branch drift for repo {RepoId} ({Slug}): stored={Stored}, detected={Detected}",
                        repo.Id,
                        repo.Slug,
                        repo.DefaultBranch,
                        detectedDefaultBranch);
                    repo.DefaultBranch = detectedDefaultBranch;
                    defaultBranchesChanged++;
                }

                await cli.RunGcAsync(diskPath, ct).ConfigureAwait(false);
                logger.LogDebug("Ran git gc on {Slug}", repo.Slug);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Git maintenance failed for repo {RepoId} ({Slug})", repo.Id, repo.Slug);
            }
        }

        if (defaultBranchesChanged > 0)
            await lightRepo.SaveChangesAsync(ct).ConfigureAwait(false);

        logger.LogInformation("Git maintenance completed for {Count} repositories", repos.Count);
    }
}
