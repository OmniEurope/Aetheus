// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data.Entities;
using Cronos;

namespace Aetheus.Back.Components.AppBackups;

internal sealed class BackupPolicyService(
    IBackupRepository repo,
    IServerRepository serverRepo,
    ITaskService taskService,
    IEncryptionService encryption,
    TimeProvider timeProvider,
    IAuditService audit,
    IEntityChangeNotifier notifier) : IBackupPolicyService
{
    public async Task<PaginatedResult<BackupPolicyDto>> GetPoliciesAsync(
        IReadOnlyCollection<int>? accessibleProjectIds, PaginationRequest request, CancellationToken ct = default)
    {
        var (page, pageSize) = request.Normalize();
        if (accessibleProjectIds is { Count: 0 })
            return new PaginatedResult<BackupPolicyDto> { Page = page, PageSize = pageSize };
        var (items, totalCount) = await repo.GetPoliciesPagedAsync(
            accessibleProjectIds, request.Search, request.SortBy, request.SortDescending,
            page, pageSize, ct, request.Filters).ConfigureAwait(false);
        return new PaginatedResult<BackupPolicyDto>
        {
            Items = items.Select(Map).ToList(),
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<BackupPolicyDto> CreateAsync(CreateBackupPolicyRequest request, CancellationToken ct = default)
    {
        ValidateCron(request.ScheduleCron);
        ValidateCron(request.RestoreCheckCron);

        var policy = new BackupPolicy
        {
            Name = request.Name.Trim(),
            ProjectId = request.ProjectId,
            ServerId = request.ServerId,
            DbEngine = request.DbEngine,
            DbHost = request.DbHost,
            DbPort = request.DbPort,
            DbName = request.DbName,
            DbUser = request.DbUser,
            DbPasswordEncrypted = string.IsNullOrEmpty(request.DbPassword) ? null : encryption.EncryptValue(request.DbPassword),
            FilePathsJson = request.FilePaths.Count > 0 ? JsonSerializer.Serialize(request.FilePaths) : null,
            ScheduleCron = request.ScheduleCron,
            RetentionCount = request.RetentionCount,
            RestoreCheckCron = request.RestoreCheckCron
        };
        repo.AddPolicy(policy);
        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.LogAsync("Created", "BackupPolicy", policy.Id, policy.Name, ct).ConfigureAwait(false);
        await BroadcastChangedAsync(policy.ProjectId, ct).ConfigureAwait(false);
        return Map(policy);
    }

    public async Task<BackupPolicyDto?> UpdateAsync(int id, UpdateBackupPolicyRequest request, CancellationToken ct = default)
    {
        ValidateCron(request.ScheduleCron);
        ValidateCron(request.RestoreCheckCron);

        var policy = await repo.FindPolicyAsync(id, ct).ConfigureAwait(false);
        if (policy is null) return null;

        policy.Name = request.Name.Trim();
        policy.Enabled = request.Enabled;
        policy.DbEngine = request.DbEngine;
        policy.DbHost = request.DbHost;
        policy.DbPort = request.DbPort;
        policy.DbName = request.DbName;
        policy.DbUser = request.DbUser;
        // null = keep existing; empty = clear; non-empty = replace.
        if (request.DbPassword is not null)
            policy.DbPasswordEncrypted = request.DbPassword.Length == 0 ? null : encryption.EncryptValue(request.DbPassword);
        policy.FilePathsJson = request.FilePaths.Count > 0 ? JsonSerializer.Serialize(request.FilePaths) : null;
        policy.ScheduleCron = request.ScheduleCron;
        policy.RetentionCount = request.RetentionCount;
        policy.RestoreCheckCron = request.RestoreCheckCron;

        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.LogAsync("Updated", "BackupPolicy", policy.Id, policy.Name, ct).ConfigureAwait(false);
        await BroadcastChangedAsync(policy.ProjectId, ct).ConfigureAwait(false);
        return Map(policy);
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken ct = default)
    {
        var policy = await repo.FindPolicyAsync(id, ct).ConfigureAwait(false);
        if (policy is null) return false;
        var projectId = policy.ProjectId;
        repo.DeletePolicy(policy);
        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.LogAsync("Deleted", "BackupPolicy", id, policy.Name, ct).ConfigureAwait(false);
        await BroadcastChangedAsync(projectId, ct).ConfigureAwait(false);
        return true;
    }

    public async Task<int?> GetOwningProjectIdAsync(int id, CancellationToken ct = default)
        => (await repo.FindPolicyAsync(id, ct).ConfigureAwait(false))?.ProjectId;

    public async Task<int?> GetServerForAgentParameterChangeAsync(
        int id, UpdateBackupPolicyRequest request, CancellationToken ct = default)
    {
        var policy = await repo.FindPolicyAsync(id, ct).ConfigureAwait(false);
        if (policy is null) return null;
        var filePathsJson = request.FilePaths.Count > 0 ? JsonSerializer.Serialize(request.FilePaths) : null;
        var changed = policy.DbEngine != request.DbEngine
            || !SameText(policy.DbHost, request.DbHost)
            || policy.DbPort != request.DbPort
            || !SameText(policy.DbName, request.DbName)
            || !SameText(policy.DbUser, request.DbUser)
            || policy.FilePathsJson != filePathsJson
            || PasswordChanges(policy.DbPasswordEncrypted, request.DbPassword);
        return changed ? policy.ServerId : null;
    }

    // An untouched empty field may come back as null or as "": neither is a new target.
    private static bool SameText(string? stored, string? requested) =>
        string.Equals(stored ?? string.Empty, requested ?? string.Empty, StringComparison.Ordinal);

    // null keeps the stored password; empty clears it; anything else replaces it.
    private bool PasswordChanges(string? storedEncrypted, string? requested) => requested switch
    {
        null => false,
        "" => storedEncrypted is not null,
        _ => storedEncrypted is null || encryption.DecryptValue(storedEncrypted) != requested
    };

    public async Task<PaginatedResult<BackupRunDto>> GetRunsAsync(
        int policyId, PaginationRequest request, CancellationToken ct = default)
    {
        var (page, pageSize) = request.Normalize();
        var (items, totalCount) = await repo.GetRunsForPolicyPagedAsync(
            policyId, request.Search, request.SortBy, request.SortDescending,
            page, pageSize, ct, request.Filters).ConfigureAwait(false);
        return new PaginatedResult<BackupRunDto>
        {
            Items = items.Select(MapRun).ToList(),
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<int> RunNowAsync(int policyId, CancellationToken ct = default)
    {
        var policy = await repo.FindPolicyAsync(policyId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"Backup policy {policyId} not found.");
        var server = await serverRepo.FindServerAsync(policy.ServerId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"Target server {policy.ServerId} not found.");
        if (server.Status != ServerStatus.Online)
            throw new ConflictException("The target server's agent is offline, so the backup cannot run now.");
        return await TriggerBackupAsync(policy, ct).ConfigureAwait(false);
    }

    public async Task<int> TriggerBackupAsync(BackupPolicy policy, CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var run = new BackupRun
        {
            BackupPolicyId = policy.Id,
            ServerId = policy.ServerId,
            Status = BackupRunStatus.Running,
            StartedAt = now
        };
        repo.AddRun(run);
        await repo.SaveChangesAsync(ct).ConfigureAwait(false); // materialise run.Id

        var env = BuildBackupEnv(policy, run.Id);
        var task = ServerTaskFactory.Operation(policy.ServerId, $"Backup - {policy.Name}",
            OperationKind.BackupExecute, policy.Id.ToString(), 1800);
        task.EnvironmentVariables = TaskEnvProtection.Protect(encryption, JsonSerializer.Serialize(env));
        await serverRepo.AddTaskAsync(task, ct).ConfigureAwait(false);
        await taskService.NotifyTaskQueuedAsync(task, ct: ct).ConfigureAwait(false);

        policy.LastRunAt = now;
        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.LogAsync("BackupTriggered", "BackupPolicy", policy.Id, null, ct).ConfigureAwait(false);
        await BroadcastChangedAsync(policy.ProjectId, ct).ConfigureAwait(false);
        return run.Id;
    }

    public async Task TriggerRestoreCheckAsync(BackupRun run, BackupPolicy policy, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(run.ArchivePath)) return; // nothing to verify

        var env = BuildBackupEnv(policy, run.Id);
        env[BackupConstants.ArchivePathEnvVar] = run.ArchivePath;

        var task = ServerTaskFactory.Operation(run.ServerId, $"Restore-check - {policy.Name}",
            OperationKind.BackupRestoreCheck, run.Id.ToString(), 1800);
        task.EnvironmentVariables = TaskEnvProtection.Protect(encryption, JsonSerializer.Serialize(env));
        await serverRepo.AddTaskAsync(task, ct).ConfigureAwait(false);
        await taskService.NotifyTaskQueuedAsync(task, ct: ct).ConfigureAwait(false);

        policy.LastRestoreCheckAt = timeProvider.GetUtcNow().UtcDateTime;
        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        await BroadcastChangedAsync(policy.ProjectId, ct).ConfigureAwait(false);
    }

    public async Task<bool> ApplyBackupResultAsync(int runId, int agentServerId, BackupExecuteResultDto result, CancellationToken ct = default)
    {
        var run = await repo.FindRunAsync(runId, ct).ConfigureAwait(false);
        if (run is null || run.ServerId != agentServerId) return false; // IDOR guard: a run belongs to its server
        run.Status = result.Success ? BackupRunStatus.Succeeded : BackupRunStatus.Failed;
        run.ArchivePath = result.ArchivePath;
        run.SizeBytes = result.SizeBytes;
        run.Sha256 = result.Sha256;
        run.Message = result.Message;
        run.CompletedAt = timeProvider.GetUtcNow().UtcDateTime;
        await SaveRunAndBroadcastAsync(run, ct).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> ApplyRestoreCheckResultAsync(int runId, int agentServerId, RestoreCheckResultDto result, CancellationToken ct = default)
    {
        var run = await repo.FindRunAsync(runId, ct).ConfigureAwait(false);
        if (run is null || run.ServerId != agentServerId) return false; // IDOR guard
        // No-fake: only a real, verified restore flips it to Verified; anything else is Failed (visible).
        run.RestoreCheckStatus = result.Verified ? RestoreCheckStatus.Verified : RestoreCheckStatus.Failed;
        run.RestoreCheckMessage = result.Message;
        run.RestoreCheckedAt = timeProvider.GetUtcNow().UtcDateTime;
        await SaveRunAndBroadcastAsync(run, ct).ConfigureAwait(false);
        return true;
    }

    private async Task SaveRunAndBroadcastAsync(BackupRun run, CancellationToken ct)
    {
        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        var policy = await repo.FindPolicyAsync(run.BackupPolicyId, ct).ConfigureAwait(false);
        if (policy is not null)
            await BroadcastChangedAsync(policy.ProjectId, ct).ConfigureAwait(false);
    }

    private async Task BroadcastChangedAsync(int projectId, CancellationToken ct)
    {
        var organizationId = await repo.GetProjectOrganizationIdAsync(projectId, ct).ConfigureAwait(false);
        await notifier.BroadcastOperationalAsync(
            ResourceType.Project,
            projectId,
            OperationalRealtimeEvents.BackupChanged,
            ct,
            organizationId).ConfigureAwait(false);
    }

    private Dictionary<string, string> BuildBackupEnv(BackupPolicy policy, int runId)
    {
        var env = new Dictionary<string, string>
        {
            [BackupConstants.EngineEnvVar] = policy.DbEngine.ToString(),
            [BackupConstants.PolicyIdEnvVar] = policy.Id.ToString(),
            [BackupConstants.RunIdEnvVar] = runId.ToString(),
            [BackupConstants.RetentionEnvVar] = policy.RetentionCount.ToString()
        };
        if (!string.IsNullOrEmpty(policy.DbHost)) env[BackupConstants.DbHostEnvVar] = policy.DbHost;
        if (policy.DbPort is not null) env[BackupConstants.DbPortEnvVar] = policy.DbPort.Value.ToString();
        if (!string.IsNullOrEmpty(policy.DbName)) env[BackupConstants.DbNameEnvVar] = policy.DbName;
        if (!string.IsNullOrEmpty(policy.DbUser)) env[BackupConstants.DbUserEnvVar] = policy.DbUser;
        if (!string.IsNullOrEmpty(policy.DbPasswordEncrypted))
            env[BackupConstants.DbPasswordEnvVar] = encryption.DecryptValue(policy.DbPasswordEncrypted);
        if (!string.IsNullOrEmpty(policy.FilePathsJson))
            env[BackupConstants.FilePathsEnvVar] = policy.FilePathsJson;
        return env;
    }

    private static void ValidateCron(string? cron)
    {
        if (string.IsNullOrWhiteSpace(cron)) return;
        try { CronExpression.Parse(cron); }
        catch (CronFormatException ex) { throw new BadRequestException($"Invalid cron expression: {ex.Message}"); }
    }

    private static BackupPolicyDto Map(BackupPolicy p) => new()
    {
        Id = p.Id,
        Name = p.Name,
        Enabled = p.Enabled,
        ProjectId = p.ProjectId,
        ProjectName = p.Project?.Name,
        ServerId = p.ServerId,
        ServerName = p.Server?.Name,
        DbEngine = p.DbEngine,
        DbHost = p.DbHost,
        DbPort = p.DbPort,
        DbName = p.DbName,
        DbUser = p.DbUser,
        HasPassword = !string.IsNullOrEmpty(p.DbPasswordEncrypted),
        FilePaths = string.IsNullOrEmpty(p.FilePathsJson) ? [] : JsonSerializer.Deserialize<List<string>>(p.FilePathsJson) ?? [],
        ScheduleCron = p.ScheduleCron,
        RetentionCount = p.RetentionCount,
        RestoreCheckCron = p.RestoreCheckCron,
        LastRunAt = p.LastRunAt,
        LastRestoreCheckAt = p.LastRestoreCheckAt,
        CreatedAt = p.CreatedAt
    };

    private static BackupRunDto MapRun(BackupRun r) => new()
    {
        Id = r.Id,
        BackupPolicyId = r.BackupPolicyId,
        ServerId = r.ServerId,
        Status = r.Status,
        SizeBytes = r.SizeBytes,
        StartedAt = r.StartedAt,
        CompletedAt = r.CompletedAt,
        RestoreCheckStatus = r.RestoreCheckStatus,
        RestoreCheckedAt = r.RestoreCheckedAt,
        RestoreCheckMessage = r.RestoreCheckMessage,
        Message = r.Message
    };
}
