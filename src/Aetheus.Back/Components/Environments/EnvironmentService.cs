// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services.DomainEvents;
using Environment = Aetheus.Back.Data.Entities.Environment;

namespace Aetheus.Back.Components.Environments;

public class EnvironmentService(
    IEnvironmentRepository repo,
    IAuditService audit,
    IEntityChangeNotifier notifier,
    IDomainEventDispatcher domainEvents,
    IEncryptionService encryption,
    IHttpContextAccessor? httpContextAccessor = null) : IEnvironmentService
{
    public async Task<PaginatedResult<EnvironmentDto>> GetEnvironmentsAsync(int? projectId, PaginationRequest request, List<int>? accessibleIds = null, CancellationToken ct = default)
    {
        var (page, pageSize) = request.Normalize();
        var (items, totalCount) = await repo.GetEnvironmentsPagedAsync(
            request.Search, projectId, page, pageSize, accessibleIds, ct,
            request.SortBy, request.SortDescending, request.Filters).ConfigureAwait(false);

        return new PaginatedResult<EnvironmentDto>
        {
            Items = items.Select(MapToDto).ToList(),
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<EnvironmentDto?> GetEnvironmentAsync(int id, CancellationToken ct = default)
    {
        var env = await repo.GetEnvironmentWithServersAsync(id, ct).ConfigureAwait(false);
        return env is null ? null : MapToDto(env);
    }

    public async Task<EnvironmentDto> CreateEnvironmentAsync(CreateEnvironmentRequest request, CancellationToken ct = default)
    {
        ValidateDastConfiguration(request.Type, request.DastEnabled, request.DastIsEphemeral,
            request.DastContainsRealData, request.DastAllowedHosts);
        var source = request.SourceEnvironmentId is { } sourceEnvironmentId
            ? await repo.GetEnvironmentForDuplicationAsync(sourceEnvironmentId, ct).ConfigureAwait(false)
                ?? throw new NotFoundException($"Source environment {sourceEnvironmentId} not found.")
            : null;
        var env = new Environment
        {
            Name = request.Name,
            Description = request.Description,
            Type = request.Type,
            ProjectId = request.ProjectId,
            RequireApproval = request.RequireApproval,
            ApprovalTimeoutMinutes = request.ApprovalTimeoutMinutes,
            ApprovalInstructions = request.ApprovalInstructions,
            DastEnabled = request.DastEnabled,
            DastIsEphemeral = request.DastIsEphemeral,
            DastContainsRealData = request.DastContainsRealData,
            DastAllowedHosts = NormalizeDastHosts(request.DastAllowedHosts),
            Servers = request.ServerIds.Select(sid => new EnvironmentServer { ServerId = sid }).ToList(),
            Checks = source?.Checks.Select(CloneCheck).ToList() ?? [],
            LinkedProjectServers = source?.LinkedProjectServers.Select(CloneProjectServerLink).ToList() ?? [],
            Libraries = source?.Libraries.Select(library => CloneLibrary(library, source.Name)).ToList() ?? [],
            Vaults = source?.Vaults.Select(vault => CloneVault(vault, source.Name)).ToList() ?? [],
            Pipelines = source?.Pipelines.Select(ClonePipeline).ToList() ?? []
        };

        await repo.AddEnvironmentAsync(env, ct).ConfigureAwait(false);
        await audit.LogAsync("Created", "Environment", env.Id, env.Name, ct).ConfigureAwait(false);
        await notifier.BroadcastAsync(ResourceType.Environment, env.Id, EntityChangeOps.Created, ct).ConfigureAwait(false);
        // An environment created by copy brings its pipelines; they are written into the project's
        // repository the same way as when an environment is linked on update.
        PublishCopiedPipelines(env);

        var result = await repo.GetEnvironmentWithServersAsync(env.Id, ct).ConfigureAwait(false);
        return MapToDto(result!);
    }

    public async Task<EnvironmentDto?> UpdateEnvironmentAsync(int id, UpdateEnvironmentRequest request, CancellationToken ct = default)
    {
        ValidateDastConfiguration(request.Type, request.DastEnabled, request.DastIsEphemeral,
            request.DastContainsRealData, request.DastAllowedHosts);
        var env = await repo.FindEnvironmentAsync(id, ct).ConfigureAwait(false);
        if (env is null) return null;

        env.Name = request.Name;
        env.Description = request.Description;
        env.Type = request.Type;
        env.ProjectId = request.ProjectId;
        env.RequireApproval = request.RequireApproval;
        env.ApprovalTimeoutMinutes = request.ApprovalTimeoutMinutes;
        env.ApprovalInstructions = request.ApprovalInstructions;
        env.DastEnabled = request.DastEnabled;
        env.DastIsEphemeral = request.DastIsEphemeral;
        env.DastContainsRealData = request.DastContainsRealData;
        env.DastAllowedHosts = NormalizeDastHosts(request.DastAllowedHosts);
        // UpdatedAt is stamped centrally by AppDbContext.SaveChangesAsync.

        // Replace servers
        env.Servers.Clear();
        env.Servers.AddRange(request.ServerIds.Select(sid => new EnvironmentServer { EnvironmentId = id, ServerId = sid }));

        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.LogAsync("Updated", "Environment", env.Id, env.Name, ct).ConfigureAwait(false);
        await notifier.BroadcastAsync(ResourceType.Environment, env.Id, EntityChangeOps.Updated, ct).ConfigureAwait(false);

        // F-007: pipeline copy is best-effort and can involve a git clone - published in the background
        // so the HTTP response returns immediately regardless of repo size/count. Pipelines handles it
        // (EnvironmentPipelinesCopyHandler): this module sits below Pipelines and never calls it.
        if (env.ProjectId is { } linkedProjectId)
            domainEvents.Publish(new EnvironmentLinkedToProjectEvent(env.Id, env.Name, linkedProjectId));

        var result = await repo.GetEnvironmentWithServersAsync(id, ct).ConfigureAwait(false);
        return MapToDto(result!);
    }

    public async Task<bool> DeleteEnvironmentAsync(int id, CancellationToken ct = default)
    {
        var env = await repo.FindEnvironmentAsync(id, ct).ConfigureAwait(false);
        if (env is null) return false;

        var name = env.Name;
        await repo.RemoveEnvironmentAsync(env, ct).ConfigureAwait(false);
        await audit.LogAsync("Deleted", "Environment", id, name, ct).ConfigureAwait(false);
        await notifier.BroadcastAsync(ResourceType.Environment, id, EntityChangeOps.Deleted, ct).ConfigureAwait(false);
        return true;
    }

    public async Task<EnvironmentDto?> DuplicateEnvironmentAsync(int envId, int? targetProjectId, CancellationToken ct = default)
    {
        var source = await repo.GetEnvironmentForDuplicationAsync(envId, ct).ConfigureAwait(false);
        if (source is null) return null;

        var projectId = targetProjectId ?? source.ProjectId;
        // English suffix - backend-generated strings follow the product default language (F-036).
        var baseName = source.Name + " (copy)";
        var name = baseName;
        var attempt = 2;
        while (await repo.NameExistsInProjectAsync(name, projectId, ct).ConfigureAwait(false))
        {
            name = $"{source.Name} ({attempt})";
            attempt++;
        }

        var clone = new Environment
        {
            Name = name,
            // A description names the environment it was written for, never its copy.
            Description = string.Empty,
            Type = source.Type,
            ProjectId = projectId,
            RequireApproval = source.RequireApproval,
            ApprovalTimeoutMinutes = source.ApprovalTimeoutMinutes,
            ApprovalInstructions = source.ApprovalInstructions,
            DastEnabled = source.DastEnabled,
            DastIsEphemeral = source.DastIsEphemeral,
            DastContainsRealData = source.DastContainsRealData,
            DastAllowedHosts = source.DastAllowedHosts,
            Servers = source.Servers.Select(es => new Data.Entities.EnvironmentServer { ServerId = es.ServerId }).ToList(),
            Checks = source.Checks.Select(CloneCheck).ToList(),
            LinkedProjectServers = source.LinkedProjectServers.Select(CloneProjectServerLink).ToList(),
            Libraries = source.Libraries.Select(library => CloneLibrary(library, source.Name)).ToList(),
            Vaults = source.Vaults.Select(vault => CloneVault(vault, source.Name)).ToList(),
            Pipelines = source.Pipelines.Select(ClonePipeline).ToList()
        };

        await repo.AddEnvironmentAsync(clone, ct).ConfigureAwait(false);
        await audit.LogAsync("Duplicated", "Environment", clone.Id, $"{clone.Name} (from {source.Name})", ct).ConfigureAwait(false);
        await notifier.BroadcastAsync(ResourceType.Environment, clone.Id, EntityChangeOps.Created, ct).ConfigureAwait(false);
        PublishCopiedPipelines(clone);

        var result = await repo.GetEnvironmentWithServersAsync(clone.Id, ct).ConfigureAwait(false);
        return MapToDto(result!);
    }

    private static Data.Entities.EnvironmentCheck CloneCheck(Data.Entities.EnvironmentCheck check) => new()
    {
        Name = check.Name,
        Type = check.Type,
        Configuration = check.Configuration,
        IsRequired = check.IsRequired,
        TimeoutSeconds = check.TimeoutSeconds
    };

    private static Data.Entities.EnvironmentProjectServer CloneProjectServerLink(
        Data.Entities.EnvironmentProjectServer link) => new() { ProjectServerId = link.ProjectServerId };

    /// <summary> The pipelines a copy brings are written into the linked project's repository by
    /// <c>EnvironmentPipelinesCopyHandler</c>, in the background, as on update. Nothing to write
    /// without a project or without pipelines. </summary>
    private void PublishCopiedPipelines(Environment env)
    {
        if (env.ProjectId is { } projectId && env.Pipelines.Count > 0)
            domainEvents.Publish(new EnvironmentLinkedToProjectEvent(env.Id, env.Name, projectId));
    }

    /// <summary>Copies library variable names with empty values to preserve structure without
    /// reusing another environment's hosts, ports or credentials. The description identifies values
    /// that require configuration.</summary>
    private static Data.Entities.VariableLibrary CloneLibrary(Data.Entities.VariableLibrary library, string sourceName) => new()
    {
        Name = library.Name,
        Description = ValuesToFillDescription(sourceName),
        Entries = library.Entries.Select(entry => new Data.Entities.VariableLibraryEntry
        {
            Key = entry.Key,
            Value = string.Empty
        }).ToList()
    };

    /// <summary>Same rule for a vault: the secret names, each holding an encrypted empty value (an empty
    /// ciphertext could not be decrypted at launch), never the source's secrets.</summary>
    private Data.Entities.Vault CloneVault(Data.Entities.Vault vault, string sourceName) => new()
    {
        Name = vault.Name,
        Description = ValuesToFillDescription(sourceName),
        Secrets = vault.Secrets.Select(secret => new Data.Entities.VaultSecret
        {
            Key = secret.Key,
            EncryptedValue = encryption.EncryptValue(string.Empty)
        }).ToList()
    };

    // English: backend-generated strings follow the product default language (F-036).
    internal static string ValuesToFillDescription(string sourceName) =>
        $"Copied from environment '{sourceName}': names only, the values are empty. Fill them in.";

    /// <summary>A copied pipeline belongs to whoever made the copy, never to the source's owner: automated
    /// runs act as the owner, so inheriting it would let the copier run a definition with someone else's
    /// rights (RBAC audit, finding 3). Without a caller the copy has no owner and its automated runs are
    /// refused until an authorized user saves it (F-EXEC-1b).</summary>
    private Data.Entities.Pipeline ClonePipeline(Data.Entities.Pipeline pipeline) => new()
    {
        Name = pipeline.Name,
        Description = pipeline.Description,
        YamlDefinition = pipeline.YamlDefinition,
        TriggerType = pipeline.TriggerType,
        CreatedByUsername = httpContextAccessor?.HttpContext?.User.Identity?.Name
    };

    public Task<EnvironmentCopyReferences?> GetCopyReferencesAsync(int id, CancellationToken ct = default)
        => repo.GetCopyReferencesAsync(id, ct);

    public Task<int?> GetProjectServerProjectIdAsync(int projectServerId, CancellationToken ct = default)
        => repo.GetProjectServerProjectIdAsync(projectServerId, ct);

    public async Task<bool> LinkProjectServerAsync(int envId, int projectServerId, CancellationToken ct = default)
    {
        var env = await repo.FindEnvironmentAsync(envId, ct).ConfigureAwait(false);
        if (env is null) return false;
        if (await repo.LinkExistsAsync(envId, projectServerId, ct).ConfigureAwait(false)) return true;

        await repo.AddLinkAsync(new Data.Entities.EnvironmentProjectServer
        {
            EnvironmentId = envId,
            ProjectServerId = projectServerId
        }, ct).ConfigureAwait(false);

        await audit.LogAsync("LinkedProjectServer", "Environment", envId, $"PS#{projectServerId}", ct).ConfigureAwait(false);
        await notifier.BroadcastAsync(ResourceType.Environment, envId, EntityChangeOps.Updated, ct).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> UnlinkProjectServerAsync(int envId, int projectServerId, CancellationToken ct = default)
    {
        if (!await repo.LinkExistsAsync(envId, projectServerId, ct).ConfigureAwait(false)) return false;
        await repo.RemoveLinkAsync(envId, projectServerId, ct).ConfigureAwait(false);
        await audit.LogAsync("UnlinkedProjectServer", "Environment", envId, $"PS#{projectServerId}", ct).ConfigureAwait(false);
        await notifier.BroadcastAsync(ResourceType.Environment, envId, EntityChangeOps.Updated, ct).ConfigureAwait(false);
        return true;
    }

    private static EnvironmentDto MapToDto(Environment e) => new()
    {
        Id = e.Id,
        Name = e.Name,
        Description = e.Description,
        Type = e.Type,
        ProjectId = e.ProjectId,
        ProjectName = e.Project?.Name,
        RequireApproval = e.RequireApproval,
        ApprovalTimeoutMinutes = e.ApprovalTimeoutMinutes,
        ApprovalInstructions = e.ApprovalInstructions,
        DastEnabled = e.DastEnabled,
        DastIsEphemeral = e.DastIsEphemeral,
        DastContainsRealData = e.DastContainsRealData,
        DastAllowedHosts = e.DastAllowedHosts,
        Servers = e.Servers.Select(es => new EnvironmentServerDto
        {
            ServerId = es.ServerId,
            ServerName = es.Server?.Name ?? string.Empty,
            ServerStatus = es.Server?.Status ?? ServerStatus.Offline
        }).ToList(),
        CreatedAt = e.CreatedAt,
        UpdatedAt = e.UpdatedAt
    };

    internal static void ValidateDastConfiguration(
        EnvironmentType type,
        bool enabled,
        bool isEphemeral,
        bool containsRealData,
        string allowedHosts)
    {
        if (!enabled) return;
        if (type == EnvironmentType.Production)
            throw new Aetheus.Back.Exceptions.BadRequestException("DAST cannot be enabled on a production environment.");
        var hosts = ParseDastHosts(allowedHosts);
        if (hosts.Count == 0)
            throw new Aetheus.Back.Exceptions.BadRequestException("DAST requires at least one explicitly allowed hostname.");
        if (containsRealData)
            throw new Aetheus.Back.Exceptions.BadRequestException("DAST cannot be enabled on an environment containing real data.");
        if (!isEphemeral || type is not (EnvironmentType.Testing or EnvironmentType.Staging))
            throw new Aetheus.Back.Exceptions.BadRequestException("DAST requires an ephemeral Testing or Staging environment.");
    }

    internal static IReadOnlyList<string> ParseDastHosts(string value)
    {
        var candidates = value
            .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(host => host.TrimEnd('.').ToLowerInvariant())
            .ToList();
        if (candidates.Count > 50)
            throw new Aetheus.Back.Exceptions.BadRequestException("DAST accepts at most 50 allowed hostnames.");
        if (candidates.Any(host => Uri.CheckHostName(host) is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6)))
            throw new Aetheus.Back.Exceptions.BadRequestException("Every DAST allowlist entry must be an exact valid hostname or IP address.");
        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string NormalizeDastHosts(string value) => string.Join('\n', ParseDastHosts(value));
}
