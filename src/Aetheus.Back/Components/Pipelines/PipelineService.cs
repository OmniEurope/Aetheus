// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Organizations;
using Aetheus.Back.Data.Entities;
using Microsoft.AspNetCore.Http;

namespace Aetheus.Back.Components.Pipelines;

public class PipelineService(
    IPipelineRepository repo,
    IPipelineGitService pipelineGit,
    IAuditService audit,
    ILogger<PipelineService> logger,
    IEntityChangeNotifier notifier,
    IHttpContextAccessor httpContextAccessor,
    TimeProvider timeProvider,
    IPipelineTemplateResolver templateResolver,
    IOrganizationRepository organizationRepository) : IPipelineService
{
    private IPipelineTemplateResolver TemplateResolver => templateResolver;
    private readonly PipelineOwnershipResolver _ownership = new(
        repo, httpContextAccessor, logger);
    public async Task<PaginatedResult<PipelineDto>> GetPipelinesAsync(PipelinePaginationRequest request, List<int>? accessibleIds = null, CancellationToken ct = default)
    {
        var (page, pageSize) = request.Normalize();
        var (items, totalCount) = await repo.GetPipelinesPagedProjectedAsync(
            request.Search, request.TriggerType, request.EnvironmentId, request.ProjectServerId, request.ProjectId,
            page, pageSize, accessibleIds, ct).ConfigureAwait(false);

        return new PaginatedResult<PipelineDto>
        {
            Items = items,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    public Task<int?> GetProjectServerProjectIdAsync(int projectServerId, CancellationToken ct = default)
        => repo.GetProjectServerProjectIdAsync(projectServerId, ct);

    public async Task<PipelineDto?> GetPipelineAsync(int id, CancellationToken ct = default)
    {
        var pipeline = await repo.GetPipelineWithRunsAsync(id, ct).ConfigureAwait(false);
        if (pipeline is null) return null;
        // Owner status determines whether chained-run authorization is available.
        var ownerActive = !string.IsNullOrWhiteSpace(pipeline.CreatedByUsername)
            && await repo.IsActiveUsernameAsync(pipeline.CreatedByUsername, ct).ConfigureAwait(false);
        return PipelineDtoMapper.Map(pipeline) with { OwnerInactive = !ownerActive };
    }

    public Task<PipelineSourceDto?> GetPipelineSourceAsync(
        int projectId, string pipelineName, CancellationToken ct = default,
        string? sourceBranch = null, int? sourceRepositoryId = null)
        => pipelineGit.GetPipelineSourceAsync(
            projectId, pipelineName, ct, sourceBranch, sourceRepositoryId);

    public async Task<(bool Found, PipelineSourceDto? Source)> GetPipelineSourceByIdAsync(int id, CancellationToken ct = default)
    {
        var fields = await repo.GetPipelineSourceFieldsAsync(id, ct).ConfigureAwait(false);
        if (fields is null) return (false, null);
        if (fields.ProjectId is not { } projectId) return (true, null);
        return (true, await pipelineGit.GetPipelineSourceAsync(
            projectId, fields.Name, ct, fields.SourceBranch, fields.SourceRepositoryId).ConfigureAwait(false));
    }

    public async Task<PipelineDependencyGroupsDto> GetDependencyGroupsAsync(
        List<int>? accessibleIds = null, int? serverId = null, int? projectId = null,
        CancellationToken ct = default)
    {
        var pipelines = await repo
            .GetPipelinesForDependencyGraphAsync(accessibleIds, serverId, projectId, ct).ConfigureAwait(false);
        return PipelineDependencyGraphBuilder.Build(pipelines, ValidateYaml);
    }

    public async Task<PaginatedResult<PipelineDependencyDto>> GetDependencyPageAsync(
        PipelinePaginationRequest request, List<int>? accessibleIds = null, CancellationToken ct = default)
    {
        var (page, pageSize) = request.Normalize();
        var (items, identities, totalCount) = await repo.GetPipelineDependencyPageAsync(request, accessibleIds, ct).ConfigureAwait(false);
        return new PaginatedResult<PipelineDependencyDto>
        {
            Items = PipelineDependencyGraphBuilder.BuildItems(items, identities, ValidateYaml),
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    /// <summary>The assigned AI profile must exist and belong to the organization that owns the
    /// pipeline: another organization's profile (and key) is never reachable from this one.</summary>
    private async Task EnsureAiProfileBelongsToPipelineOrganizationAsync(
        int? aiRunnerProfileId, int? projectId, int? environmentId, int? projectServerId, CancellationToken ct)
    {
        if (aiRunnerProfileId is not { } profileId) return;
        var profileOrganizationId = await repo.GetAiRunnerProfileOrganizationIdAsync(profileId, ct).ConfigureAwait(false)
            ?? throw new BadRequestException("AI runner profile not found.");
        var pipelineOrganizationId = await repo
            .GetPipelineOwnerOrganizationIdAsync(projectId, environmentId, projectServerId, ct).ConfigureAwait(false);
        if (pipelineOrganizationId != profileOrganizationId)
            throw new BadRequestException("The AI runner profile and the pipeline must belong to the same organization.");
    }

    public async Task<PipelineDto> CreatePipelineAsync(CreatePipelineRequest request, CancellationToken ct = default)
    {
        await EnsureAiProfileBelongsToPipelineOrganizationAsync(
            request.AiRunnerProfileId, request.ProjectId, request.EnvironmentId, request.ProjectServerId, ct)
            .ConfigureAwait(false);
        var pipeline = new Pipeline
        {
            Name = request.Name,
            Description = request.Description,
            YamlDefinition = request.YamlDefinition,
            TriggerType = ParseTriggerType(request.YamlDefinition),
            ProjectId = request.ProjectId,
            SourceRepositoryId = request.SourceRepositoryId,
            SourceBranch = request.SourceBranch?.Trim(),
            EnvironmentId = request.EnvironmentId,
            ProjectServerId = request.ProjectServerId,
            AiRunnerProfileId = request.AiRunnerProfileId,
            // F-EXEC-1b: record the creator so webhook/scheduler runs can be authorized
            // against a principal (interactive runs authorize the live caller instead).
            CreatedByUsername = _ownership.CurrentUsername
        };
        PipelineTemplateReferenceMetadata.Apply(pipeline, request.YamlDefinition);
        // Git-first: commit to the project's internal repo BEFORE persisting the DB mirror, so a git
        // failure aborts the create instead of leaving a DB-only definition that diverges from git.
        await PipelineAuthoritativeGitCoordinator.CommitCreateAsync(
            pipeline, pipelineGit, logger, _ownership.CurrentUsername ?? "system", ct).ConfigureAwait(false);
        await repo.AddPipelineAsync(pipeline, ct).ConfigureAwait(false);
        await PipelineAuthoritativeGitCoordinator.CopyToLinkedEnvironmentProjectAsync(
            pipeline, repo, pipelineGit, logger, _ownership.CurrentUsername ?? "system", ct).ConfigureAwait(false);
        await audit.LogAsync("Created", "Pipeline", pipeline.Id, pipeline.Name, ct).ConfigureAwait(false);
        await BroadcastPipelineChangeAsync(pipeline, EntityChangeOps.Created, ct).ConfigureAwait(false);
        return PipelineDtoMapper.Map(pipeline);
    }

    public async Task<PipelineDto?> UpdatePipelineAsync(int id, UpdatePipelineRequest request, CancellationToken ct = default)
    {
        var pipeline = await repo.FindPipelineAsync(id, ct).ConfigureAwait(false);
        if (pipeline is null) return null;
        await EnsureAiProfileBelongsToPipelineOrganizationAsync(
            request.AiRunnerProfileId, request.ProjectId, request.EnvironmentId, request.ProjectServerId, ct)
            .ConfigureAwait(false);

        var snapshot = PipelineStateSnapshot.Capture(pipeline);
        var previousLocation = snapshot.ProjectId is { } oldProjectId
            ? new PipelineGitDefinitionLocation(
                oldProjectId, snapshot.Name, snapshot.SourceBranch, snapshot.SourceRepositoryId)
            : null;
        var nextBranch = request.SourceBranch?.Trim();
        var nextLocation = request.ProjectId is { } newProjectId
            ? new PipelineGitDefinitionLocation(
                newProjectId, request.Name, nextBranch, request.SourceRepositoryId)
            : null;
        var actor = _ownership.CurrentUsername ?? "system";

        // Git is authoritative. Rename/move and content replacement happen before the DB mirror and
        // use one commit when source and destination share a repo/branch.
        var (gitOutcome, gitError) = await pipelineGit.ApplyProjectPipelineChangeAsync(
            previousLocation, nextLocation, request.YamlDefinition, actor, ct).ConfigureAwait(false);
        if (gitOutcome == GitWriteOutcome.Failed)
            throw new ConflictException(
                $"Pipeline definition could not be updated in the project's git repository: {gitError}");

        pipeline.Name = request.Name;
        pipeline.Description = request.Description;
        pipeline.YamlDefinition = request.YamlDefinition;
        PipelineTemplateReferenceMetadata.Apply(pipeline, request.YamlDefinition);
        pipeline.TriggerType = ParseTriggerType(request.YamlDefinition);
        pipeline.ProjectId = request.ProjectId;
        pipeline.SourceRepositoryId = request.SourceRepositoryId;
        pipeline.SourceBranch = nextBranch;
        pipeline.EnvironmentId = request.EnvironmentId;
        pipeline.ProjectServerId = request.ProjectServerId;
        pipeline.AiRunnerProfileId = request.AiRunnerProfileId;
        pipeline.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        // F-EXEC-1b: adopt ownership when a legacy/unowned pipeline is re-saved by an
        // authorized user - this is the documented remediation to re-enable its automated
        // (webhook/scheduler) triggers. Existing ownership is never silently reassigned.
        if (string.IsNullOrEmpty(pipeline.CreatedByUsername))
            pipeline.CreatedByUsername = _ownership.CurrentUsername;

        try
        {
            await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            snapshot.Restore(pipeline);
            if (gitOutcome == GitWriteOutcome.Committed)
            {
                var (rollbackOutcome, rollbackError) = await pipelineGit.ApplyProjectPipelineChangeAsync(
                    nextLocation, previousLocation, snapshot.YamlDefinition, actor, ct).ConfigureAwait(false);
                if (rollbackOutcome != GitWriteOutcome.Committed)
                    throw new ConflictException(
                        $"Database update failed and the authoritative Git change could not be rolled back: {rollbackError}");
            }
            throw;
        }
        await PipelineAuthoritativeGitCoordinator.CopyToLinkedEnvironmentProjectAsync(
            pipeline, repo, pipelineGit, logger, _ownership.CurrentUsername ?? "system", ct).ConfigureAwait(false);
        await audit.LogAsync("Updated", "Pipeline", pipeline.Id, pipeline.Name, ct).ConfigureAwait(false);
        await BroadcastPipelineChangeAsync(pipeline, EntityChangeOps.Updated, ct).ConfigureAwait(false);
        return PipelineDtoMapper.Map(pipeline);
    }

    public async Task<bool> DeletePipelineAsync(int id, CancellationToken ct = default)
    {
        var pipeline = await repo.FindPipelineAsync(id, ct).ConfigureAwait(false);
        if (pipeline is null) return false;
        var name = pipeline.Name;
        var location = pipeline.ProjectId is { } projectId
            ? new PipelineGitDefinitionLocation(
                projectId, pipeline.Name, pipeline.SourceBranch, pipeline.SourceRepositoryId)
            : null;
        var actor = _ownership.CurrentUsername ?? "system";
        var (gitOutcome, gitError) = await pipelineGit.ApplyProjectPipelineChangeAsync(
            location, null, null, actor, ct).ConfigureAwait(false);
        if (gitOutcome == GitWriteOutcome.Failed)
            throw new ConflictException(
                $"Pipeline definition could not be deleted from the project's git repository: {gitError}");
        try
        {
            await repo.RemovePipelineAsync(pipeline, ct).ConfigureAwait(false);
        }
        catch
        {
            if (gitOutcome == GitWriteOutcome.Committed)
            {
                var (rollbackOutcome, rollbackError) = await pipelineGit.ApplyProjectPipelineChangeAsync(
                    null, location, pipeline.YamlDefinition, actor, ct).ConfigureAwait(false);
                if (rollbackOutcome != GitWriteOutcome.Committed)
                    throw new ConflictException(
                        $"Database delete failed and the authoritative Git deletion could not be rolled back: {rollbackError}");
            }
            throw;
        }
        await audit.LogAsync("Deleted", "Pipeline", id, name, ct).ConfigureAwait(false);
        await BroadcastPipelineChangeAsync(pipeline, EntityChangeOps.Deleted, ct).ConfigureAwait(false);
        return true;
    }


    public PipelineYamlDefinition? ValidateYaml(string yaml) =>
        YamlParsingHelper.ParseAndValidate(yaml, logger);

    // P-23: Strict YAML validation with warnings for unknown properties
    public YamlValidationResultDto ValidateYamlStrict(string yaml)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        PipelineYamlDefinition? definition;
        try
        {
            definition = YamlParsingHelper.Deserializer.Deserialize<PipelineYamlDefinition>(yaml);
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            return new YamlValidationResultDto
            {
                IsValid = false,
                Errors = [$"YAML syntax error: {ex.Message}"]
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
        {
            return new YamlValidationResultDto
            {
                IsValid = false,
                Errors = [$"Parsing error: {ex.Message}"]
            };
        }

        if (definition is null)
        {
            return new YamlValidationResultDto { IsValid = false, Errors = ["Empty YAML definition."] };
        }

        PipelineDefinitionValidator.ValidateDefinitionBasics(definition, errors, warnings);
        PipelineDefinitionValidator.ValidateStages(definition, errors, warnings);

        var effectiveStages = YamlParsingHelper.FlattenJobs(definition);
        errors.AddRange(PipelineRunHelpers.ValidateMatrixValues(effectiveStages));
        errors.AddRange(PipelineRunHelpers.ValidateIsolationDefinitions(definition.Isolation, effectiveStages));
        errors.AddRange(PipelineRunHelpers.ValidateIsolationLimits(effectiveStages));
        errors.AddRange(PipelineRunHelpers.ValidateExecutionRoles(effectiveStages));
        errors.AddRange(PipelineAnalysisGateOrderingValidator.Validate(effectiveStages));

        // A misspelled known key (a guard such as approval_timeout_minutes) is refused, never skipped.
        errors.AddRange(PipelineYamlDiagnostics.NearMissKeyErrors(yaml, logger));
        PipelineYamlDiagnostics.AppendUnknownPropertyWarnings(yaml, warnings, logger);

        return new YamlValidationResultDto
        {
            IsValid = errors.Count == 0,
            Definition = errors.Count == 0 ? definition : null,
            Errors = errors,
            Warnings = warnings
        };
    }

    public async Task<YamlValidationResultDto> ValidateYamlStrictAsync(
        string yaml, int? projectId, int? environmentId, int? projectServerId,
        int? organizationId = null, CancellationToken ct = default)
    {
        var resolvedOrganizationId = organizationId
            ?? await repo.GetPipelineOwnerOrganizationIdAsync(projectId, environmentId, projectServerId, ct)
                .ConfigureAwait(false)
            ?? await organizationRepository.GetDefaultOrganizationIdAsync(ct).ConfigureAwait(false);
        if (resolvedOrganizationId is null)
            return new YamlValidationResultDto
            {
                IsValid = false,
                Errors = ["No organization is available for template resolution."]
            };

        try
        {
            // No queue-time values: this is a save, not a launch. Passing an empty set instead made
            // the resolver answer the launch question ("can this start now") and reject any pipeline
            // declaring a required parameter without a default - a shape the repository-sync path
            // stores happily and that aetheus-deploy-prod actually ships. The declaration itself is
            // still validated below; a missing value is refused where it matters, in TriggerRun.
            var resolution = await TemplateResolver.ResolveAsync(
                yaml, resolvedOrganizationId.Value, parameters: null, ct).ConfigureAwait(false);
            var structural = ValidateYamlStrict(resolution.Yaml);
            var declarationErrors = PipelineParameterResolver.ValidateDeclarations(resolution.Definition.Parameters);
            if (declarationErrors.Count > 0)
                return new YamlValidationResultDto { IsValid = false, Errors = declarationErrors };
            var sourceWarnings = ValidateYamlStrict(yaml).Warnings;
            var warnings = structural.Warnings.Concat(sourceWarnings).Distinct(StringComparer.Ordinal).ToList();
            if (resolution.UsesLegacyReference)
                warnings.Add($"Template reference '{resolution.TemplateName}' is unpinned; use @{resolution.ResolvedVersion}.");
            return structural with
            {
                Definition = structural.IsValid ? resolution.Definition : null,
                Warnings = warnings
            };
        }
        catch (BadRequestException exception)
        {
            return new YamlValidationResultDto { IsValid = false, Errors = [exception.Message] };
        }
    }

    private PipelineTriggerType ParseTriggerType(string yaml)
    {
        var def = ValidateYaml(yaml);
        return def?.Trigger?.ToLowerInvariant() switch
        {
            "webhook" => PipelineTriggerType.Webhook,
            "schedule" => PipelineTriggerType.Schedule,
            _ => PipelineTriggerType.Manual
        };
    }

    public async Task<List<PipelineDto>> GetWebhookTriggeredPipelinesForProjectAsync(int projectId, CancellationToken ct = default)
    {
        var pipelines = await repo.GetWebhookTriggeredPipelinesForProjectAsync(projectId, ct).ConfigureAwait(false);
        return pipelines.Select(p => new PipelineDto
        {
            Id = p.Id,
            Name = p.Name,
            TriggerType = p.TriggerType,
            ProjectId = p.ProjectId,
            SourceRepositoryId = p.SourceRepositoryId,
            SourceBranch = p.SourceBranch,
            YamlDefinition = p.YamlDefinition,
            CreatedAt = p.CreatedAt,
            UpdatedAt = p.UpdatedAt
        }).ToList();
    }

    public Task<bool> HasActiveRunAsync(int pipelineId, CancellationToken ct = default)
        => repo.HasActiveRunAsync(pipelineId, ct);

    public async Task<PipelineDto> UpsertPipelineFromYamlAsync(
        string name, string yamlContent, int projectId, string triggerType,
        CancellationToken ct = default, string? sourceBranch = null, string? defaultBranch = null,
        int? sourceRepositoryId = null)
    {
        var configuredSourceBranch = ValidateYaml(yamlContent)?.SourceBranch?.Trim();
        var existing = await repo.FindPipelineByNameAndProjectAsync(name, projectId, ct).ConfigureAwait(false);
        return existing is null
            ? await CreateSyncedPipelineAsync(
                name, yamlContent, projectId, triggerType, configuredSourceBranch,
                sourceBranch, defaultBranch, sourceRepositoryId, ct).ConfigureAwait(false)
            : await UpdateSyncedPipelineAsync(
                existing, name, yamlContent, projectId, triggerType, configuredSourceBranch,
                sourceBranch, defaultBranch, sourceRepositoryId, ct).ConfigureAwait(false);
    }

    private async Task<PipelineDto> CreateSyncedPipelineAsync(
        string name,
        string yamlContent,
        int projectId,
        string triggerType,
        string? configuredSourceBranch,
        string? sourceBranch,
        string? defaultBranch,
        int? sourceRepositoryId,
        CancellationToken ct)
    {
        var owner = await _ownership.ResolveAsync(projectId, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(owner))
            throw _ownership.MissingOwner(projectId, name);
        var pipeline = new Pipeline
        {
            Name = name,
            YamlDefinition = yamlContent,
            SourceBranch = NormalizeSourceBranch(configuredSourceBranch ?? sourceBranch, defaultBranch),
            TriggerType = ParseTriggerTypeValue(triggerType),
            ProjectId = projectId,
            SourceRepositoryId = sourceRepositoryId,
            CreatedByUsername = owner
        };
        PipelineTemplateReferenceMetadata.Apply(pipeline, yamlContent);
        await repo.AddPipelineAsync(pipeline, ct).ConfigureAwait(false);
        await BroadcastPipelineChangeAsync(pipeline, EntityChangeOps.Created, ct).ConfigureAwait(false);
        return PipelineDtoMapper.Map(pipeline);
    }

    private async Task<PipelineDto> UpdateSyncedPipelineAsync(
        Pipeline existing,
        string name,
        string yamlContent,
        int projectId,
        string triggerType,
        string? configuredSourceBranch,
        string? sourceBranch,
        string? defaultBranch,
        int? sourceRepositoryId,
        CancellationToken ct)
    {
        EnsureRepositoryBinding(existing, name, sourceRepositoryId);
        if (!MatchesRequestedSourceBranch(existing, configuredSourceBranch, sourceBranch, defaultBranch))
            return PipelineDtoMapper.Map(existing);
        existing.YamlDefinition = yamlContent;
        PipelineTemplateReferenceMetadata.Apply(existing, yamlContent);
        existing.SourceRepositoryId = sourceRepositoryId ?? existing.SourceRepositoryId;
        if (!string.IsNullOrWhiteSpace(configuredSourceBranch))
            existing.SourceBranch = NormalizeSourceBranch(configuredSourceBranch, defaultBranch);
        existing.TriggerType = ParseTriggerTypeValue(triggerType);
        existing.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        await UpdateSyncOwnershipAsync(existing, projectId, name, ct).ConfigureAwait(false);
        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        await BroadcastPipelineChangeAsync(existing, EntityChangeOps.Updated, ct).ConfigureAwait(false);
        return PipelineDtoMapper.Map(existing);
    }

    private static void EnsureRepositoryBinding(Pipeline existing, string name, int? sourceRepositoryId)
    {
        if (sourceRepositoryId is { } incomingRepositoryId
            && existing.SourceRepositoryId is { } currentRepositoryId
            && incomingRepositoryId != currentRepositoryId)
            throw new ConflictException(
                $"Pipeline '{name}' is already bound to source repository {currentRepositoryId}; repository {incomingRepositoryId} cannot replace it implicitly.");
    }

    private static bool MatchesRequestedSourceBranch(
        Pipeline existing,
        string? configuredSourceBranch,
        string? sourceBranch,
        string? defaultBranch)
    {
        if (string.IsNullOrWhiteSpace(sourceBranch)) return true;
        if (!string.IsNullOrWhiteSpace(configuredSourceBranch)
            && string.Equals(configuredSourceBranch, sourceBranch, StringComparison.Ordinal))
            return true;
        var effectiveExistingBranch = string.IsNullOrWhiteSpace(existing.SourceBranch)
            ? defaultBranch
            : existing.SourceBranch;
        return string.Equals(effectiveExistingBranch, sourceBranch, StringComparison.Ordinal);
    }

    private async Task UpdateSyncOwnershipAsync(
        Pipeline pipeline,
        int projectId,
        string name,
        CancellationToken ct)
    {
        var syncActor = _ownership.CurrentUsername;
        if (!string.IsNullOrEmpty(syncActor))
        {
            if (string.Equals(syncActor, pipeline.CreatedByUsername, StringComparison.Ordinal)) return;
            logger.LogInformation(
                "Transferred pipeline '{Name}' (id {Id}) automated-run ownership from '{Old}' to git revision author '{New}'",
                pipeline.Name, pipeline.Id, pipeline.CreatedByUsername, syncActor);
            pipeline.CreatedByUsername = syncActor;
            return;
        }
        if (!await _ownership.IsUnresolvableAsync(pipeline.CreatedByUsername, ct).ConfigureAwait(false)) return;
        var repaired = await _ownership.ResolveAsync(projectId, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(repaired)) throw _ownership.MissingOwner(projectId, name);
        pipeline.CreatedByUsername = repaired;
    }

    private static string? NormalizeSourceBranch(string? branch, string? defaultBranch) =>
        string.IsNullOrWhiteSpace(branch)
        || string.Equals(branch, defaultBranch, StringComparison.Ordinal)
            ? null
            : branch;

    private static PipelineTriggerType ParseTriggerTypeValue(string triggerType) =>
        triggerType.Equals("webhook", StringComparison.OrdinalIgnoreCase)
            ? PipelineTriggerType.Webhook
            : triggerType.Equals("schedule", StringComparison.OrdinalIgnoreCase)
                ? PipelineTriggerType.Schedule
                : PipelineTriggerType.Manual;

    private async Task BroadcastPipelineChangeAsync(Pipeline pipeline, string operation, CancellationToken ct)
    {
        var organizationId = await repo.GetPipelineOwnerOrganizationIdAsync(
            pipeline.ProjectId, pipeline.EnvironmentId, pipeline.ProjectServerId, ct).ConfigureAwait(false);
        await notifier.BroadcastAsync(
            ResourceType.Pipeline, pipeline.Id, operation, ct, organizationId).ConfigureAwait(false);
    }

}
