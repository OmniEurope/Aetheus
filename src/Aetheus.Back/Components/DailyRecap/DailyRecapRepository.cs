// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.DailyRecap;

internal sealed class DailyRecapRepository(AppDbContext db) : IDailyRecapRepository
{
    private const string AdministratorRole = "Admin";

    public async Task<IReadOnlyDictionary<string, string>> GetSettingRowsAsync(CancellationToken ct = default)
    {
        var rows = await db.AppSettings.AsNoTracking()
            .Where(setting => setting.Key.StartsWith(DailyRecapSettingKeys.Prefix))
            .Select(setting => new { setting.Key, setting.Value })
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.ToDictionary(row => row.Key, row => row.Value, StringComparer.Ordinal);
    }

    public async Task SaveSettingRowsAsync(IReadOnlyDictionary<string, string> rows, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var keys = rows.Keys.ToList();
        var existing = await db.AppSettings
            .Where(setting => keys.Contains(setting.Key))
            .ToDictionaryAsync(setting => setting.Key, StringComparer.Ordinal, ct).ConfigureAwait(false);
        foreach (var (key, value) in rows)
        {
            if (existing.TryGetValue(key, out var setting))
            {
                setting.Value = value;
                setting.UpdatedAt = nowUtc;
            }
            else
            {
                db.AppSettings.Add(new AppSetting { Key = key, Value = value, UpdatedAt = nowUtc });
            }
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public Task<List<string>> GetActiveAdministratorEmailsAsync(CancellationToken ct = default) =>
        db.Users.AsNoTracking()
            .Where(user => user.IsActive && user.Email != ""
                && user.UserRoles.Any(link => link.Role.Name == AdministratorRole))
            .OrderBy(user => user.Username)
            .Select(user => user.Email)
            .ToListAsync(ct);

    public Task<List<RecapPipelineRun>> GetParentRunsCompletedAsync(
        DateTime startUtc, DateTime endUtc, int limit, CancellationToken ct = default)
    {
        var terminal = PipelineStatusFacts.Terminal;
        // A child is a run a trigger step launched (PipelineStepRun.TriggeredRunId), the same rule as the
        // runs list's grouping: its outcome is already carried by its parent's status.
        return db.PipelineRuns.AsNoTracking()
            .Where(run => run.CompletedAt >= startUtc && run.CompletedAt < endUtc
                && terminal.Contains(run.Status)
                && !db.PipelineStepRuns.Any(step => step.TriggeredRunId == run.Id))
            .OrderBy(run => run.CompletedAt)
            .ThenBy(run => run.Id)
            .Take(limit)
            .Select(run => new RecapPipelineRun(
                run.Id,
                run.Pipeline.Name,
                run.Pipeline.Project != null ? run.Pipeline.Project.Name : null,
                run.Status,
                run.CompletedAt!.Value,
                run.BranchName))
            .ToListAsync(ct);
    }

    public Task<List<RecapServerFacts>> GetServerFactsAsync(CancellationToken ct = default) =>
        db.Servers.AsNoTracking()
            .OrderBy(server => server.Name)
            .Select(server => new
            {
                server.Id,
                server.Name,
                server.Status,
                server.LastHeartbeat,
                server.AgentVersion,
                Updates = server.SecurityUpdatesState,
                Disk = db.ServerMetrics
                    .Where(metric => metric.ServerId == server.Id)
                    .OrderByDescending(metric => metric.Timestamp)
                    .Select(metric => new { metric.DiskUsedGb, metric.DiskTotalGb, metric.Timestamp })
                    .FirstOrDefault()
            })
            .Select(row => new RecapServerFacts(
                row.Id,
                row.Name,
                row.Status,
                row.LastHeartbeat,
                row.AgentVersion,
                row.Updates == null ? null : new RecapOsUpdates(row.Updates.Probed, row.Updates.PendingTotal, row.Updates.PendingSecurity),
                row.Disk == null ? null : (double?)row.Disk.DiskUsedGb,
                row.Disk == null ? null : (double?)row.Disk.DiskTotalGb,
                row.Disk == null ? null : (DateTime?)row.Disk.Timestamp))
            .ToListAsync(ct);

    public Task<List<ServerDto>> GetAgentCompatibilityFactsAsync(CancellationToken ct = default) =>
        ServerCompatibilityFacts.ReadAsync(db.Servers, null, ct);

    public async Task<List<RecapRelease>> GetReleaseEventsAsync(DateTime startUtc, DateTime endUtc, CancellationToken ct = default)
    {
        var rows = await db.Releases.AsNoTracking()
            .Where(release => (release.PromotedAt >= startUtc && release.PromotedAt < endUtc)
                || (release.RolledBackAt >= startUtc && release.RolledBackAt < endUtc))
            .Select(release => new { ProjectName = release.Project.Name, release.Version, release.PromotedAt, release.RolledBackAt })
            .ToListAsync(ct).ConfigureAwait(false);
        var events = new List<RecapRelease>();
        foreach (var row in rows)
        {
            if (row.PromotedAt is { } promoted && promoted >= startUtc && promoted < endUtc)
                events.Add(new RecapRelease(row.ProjectName, row.Version, ReleaseStatus.Promoted, promoted));
            if (row.RolledBackAt is { } rolledBack && rolledBack >= startUtc && rolledBack < endUtc)
                events.Add(new RecapRelease(row.ProjectName, row.Version, ReleaseStatus.RolledBack, rolledBack));
        }
        return [.. events.OrderBy(item => item.AtUtc)];
    }

    public Task<List<RecapBackup>> GetBackupsFinishedAsync(DateTime startUtc, DateTime endUtc, CancellationToken ct = default) =>
        db.BackupRuns.AsNoTracking()
            .Where(run => run.CompletedAt >= startUtc && run.CompletedAt < endUtc && run.Status != BackupRunStatus.Running)
            .OrderBy(run => run.CompletedAt)
            .Select(run => new RecapBackup(
                run.BackupPolicy.Name,
                run.BackupPolicy.Project.Name,
                run.BackupPolicy.Server.Name,
                run.Status,
                run.CompletedAt!.Value,
                run.Message))
            .ToListAsync(ct);

    public Task<List<RecapExpiring>> GetCertificatesExpiringBeforeAsync(DateTime limitUtc, CancellationToken ct = default) =>
        db.CertbotCertificates.AsNoTracking()
            .Where(certificate => certificate.ExpiryDate < limitUtc)
            .OrderBy(certificate => certificate.ExpiryDate)
            .Select(certificate => new RecapExpiring(certificate.Name, certificate.Server.Name, certificate.ExpiryDate))
            .ToListAsync(ct);

    public Task<List<RecapExpiring>> GetSecretsExpiringBeforeAsync(DateTime limitUtc, CancellationToken ct = default) =>
        db.VaultSecrets.AsNoTracking()
            .Where(secret => secret.ExpiresAt != null && secret.ExpiresAt < limitUtc)
            .OrderBy(secret => secret.ExpiresAt)
            .Select(secret => new RecapExpiring(secret.Key, secret.Vault.Name, secret.ExpiresAt!.Value))
            .ToListAsync(ct);

    public async Task<RecapFindings> GetFindingChangesAsync(DateTime startUtc, DateTime endUtc, CancellationToken ct = default)
    {
        var appeared = await db.AnalysisFindings.AsNoTracking()
            .Where(finding => finding.FirstSeenAt >= startUtc && finding.FirstSeenAt < endUtc)
            .GroupBy(finding => finding.Severity)
            .Select(group => new { Severity = group.Key, Count = group.Count() })
            .ToListAsync(ct).ConfigureAwait(false);
        var resolved = await db.AnalysisFindings.AsNoTracking()
            .CountAsync(finding => finding.ResolvedAt >= startUtc && finding.ResolvedAt < endUtc, ct).ConfigureAwait(false);
        return new RecapFindings(appeared.ToDictionary(row => row.Severity, row => row.Count), resolved);
    }

    public Task<List<RecapMailServer>> GetMailServersAsync(CancellationToken ct = default) =>
        db.MailStates.AsNoTracking()
            .OrderBy(state => state.Server.Name)
            .Select(state => new RecapMailServer(
                state.Server.Name, state.IsPostfixRunning, state.IsDovecotRunning, state.QueueSize, state.LastUpdated))
            .ToListAsync(ct);
}
