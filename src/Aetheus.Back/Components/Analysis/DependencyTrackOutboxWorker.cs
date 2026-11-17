// SPDX-License-Identifier: EUPL-1.2
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Aetheus.Back.Components.Artifacts;
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Data.Entities;
using Microsoft.Extensions.Options;

namespace Aetheus.Back.Components.Analysis;

public sealed class DependencyTrackOutboxWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<DependencyTrackOptions> options,
    TimeProvider timeProvider,
    ILogger<DependencyTrackOutboxWorker> logger,
    IPostgresLeaderLease leaderLease) : LeaderBackgroundService(leaderLease, "aetheus:dependency-track-outbox")
{
    private const long MaximumPayloadBytes = 104_857_600;
    private readonly DependencyTrackOptions _options = options.Value;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        _options.Enabled ? base.ExecuteAsync(stoppingToken) : Task.CompletedTask;

    protected override async Task RunLeaderLoopAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(
            TimeSpan.FromSeconds(Math.Clamp(_options.OutboxPollSeconds, 5, 300)),
            timeProvider);
        do
        {
            await RunCycleSafelyAsync(stoppingToken).ConfigureAwait(false);
        } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task RunCycleSafelyAsync(CancellationToken ct)
    {
        try { await RunCycleAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogError(exception, "Dependency-Track outbox cycle failed");
        }
    }

    internal async Task RunCycleAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<DependencyTrackOutboxRepository>();
        var processor = scope.ServiceProvider.GetRequiredService<IDependencyTrackSubmissionProcessor>();
        var storage = scope.ServiceProvider.GetRequiredService<IArtifactStorageService>();
        var notifications = scope.ServiceProvider.GetRequiredService<INotificationService>();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var items = await repository.GetDueAsync(now, 20, ct).ConfigureAwait(false);
        foreach (var item in items)
        {
            try
            {
                var payload = await repository.GetPayloadAsync(item.Id, ct).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The Dependency-Track outbox payload is unavailable.");
                var content = await ReadPayloadAsync(storage, payload, ct).ConfigureAwait(false);
                await processor.ProcessSbomAsync(
                    new AnalysisReportDtoContext(
                        payload.ReportId,
                        payload.OrganizationId,
                        payload.ProjectId,
                        payload.ExternalProjectName,
                        payload.ProjectVersion),
                    content,
                    ct).ConfigureAwait(false);
                await repository.MarkSucceededAsync(
                    item,
                    timeProvider.GetUtcNow().UtcDateTime,
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                await HandleFailureAsync(repository, notifications, item, exception, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleFailureAsync(
        DependencyTrackOutboxRepository repository,
        INotificationService notifications,
        DependencyTrackOutboxItem item,
        Exception exception,
        CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var nextAttempt = item.AttemptCount + 1;
        var terminal = nextAttempt >= Math.Clamp(_options.MaxSubmissionAttempts, 1, 20);
        var delaySeconds = Math.Min(
            3600,
            Math.Clamp(_options.RetryBaseSeconds, 1, 3600) * Math.Pow(2, Math.Min(nextAttempt - 1, 10)));
        await repository.MarkFailedAsync(
            item,
            exception.Message,
            terminal,
            now.AddSeconds(delaySeconds),
            now,
            ct).ConfigureAwait(false);
        if (terminal)
        {
            await notifications.SendEventAsync("analysis.cve-sync.failed", new
            {
                item.OrganizationId,
                item.ProjectId,
                AnalysisReportId = item.AnalysisReportId,
                Status = DependencyTrackOutboxStatuses.Failed,
                item.LastError,
                item.AttemptCount
            }, ct).ConfigureAwait(false);
            logger.LogError(
                exception,
                "Dependency-Track outbox item {OutboxItemId} failed permanently after {AttemptCount} attempts",
                item.Id,
                item.AttemptCount);
        }
        else
        {
            logger.LogWarning(
                exception,
                "Dependency-Track outbox item {OutboxItemId} will retry at {NextAttemptAt}",
                item.Id,
                item.NextAttemptAt);
        }
    }

    private static async Task<string> ReadPayloadAsync(
        IArtifactStorageService storage,
        DependencyTrackOutboxPayload payload,
        CancellationToken ct)
    {
        await using var artifact = storage.OpenArtifact(payload.ArtifactFilePath)
            ?? throw new FileNotFoundException("The immutable SBOM artifact is missing.");
        using var archive = new ZipArchive(artifact, ZipArchiveMode.Read, leaveOpen: false);
        var expectedPath = payload.ReportEntryPath.Replace('\\', '/').TrimStart('/');
        var entry = archive.Entries.SingleOrDefault(candidate =>
            string.Equals(candidate.FullName.Replace('\\', '/').TrimStart('/'), expectedPath, StringComparison.Ordinal))
            ?? throw new InvalidDataException("The immutable SBOM artifact contains no matching report entry.");
        if (entry.Length != payload.ContentSize || entry.Length is <= 0 or > MaximumPayloadBytes)
            throw new InvalidDataException("The immutable SBOM artifact size does not match the ingested report.");

        var bytes = new byte[checked((int)entry.Length)];
        await using (var input = entry.Open())
        {
            await input.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
            if (input.ReadByte() != -1)
                throw new InvalidDataException("The immutable SBOM artifact exceeds its declared entry size.");
        }
        var actualHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!string.Equals(actualHash, payload.ContentHash, StringComparison.Ordinal))
            throw new InvalidDataException("The immutable SBOM artifact hash does not match the ingested report.");
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
    }
}
