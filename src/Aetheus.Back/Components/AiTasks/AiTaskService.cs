// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using System.Text.RegularExpressions;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Organizations;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data.Entities;
using Cronos;

namespace Aetheus.Back.Components.AiTasks;

public sealed partial class AiTaskService(
    IAiTaskRepository repo,
    IOrganizationRepository organizations,
    IAuditService audit,
    IEncryptionService encryption,
    ISecretMaskingService secretMasking,
    ITaskQueueNotifier taskQueueNotifier,
    IGitLightService gitLightService,
    TimeProvider timeProvider,
    IConfiguration configuration,
    ILogger<AiTaskService> logger) : IAiTaskService
{
    private const int MaxArguments = 64;
    private const int MaxEventTypes = 16;
    private const int MaxConcurrentRunsPerServer = 2;
    private static readonly TimeSpan EventCooldown = TimeSpan.FromMinutes(10);

    public async Task<PaginatedResult<AiRunnerProfileDto>> GetProfilesAsync(
        PaginationRequest request, CancellationToken ct)
    {
        var (page, pageSize) = request.Normalize();
        var (items, total) = await repo.GetProfilesPageAsync(
            request.Search, page, pageSize, ct, request.Filters,
            request.SortBy, request.SortDescending).ConfigureAwait(false);
        return new PaginatedResult<AiRunnerProfileDto>
        {
            Items = items.Select(MapProfile).ToList(),
            TotalCount = total,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<AiRunnerProfileDto?> GetProfileAsync(int id, CancellationToken ct)
    {
        var profile = await repo.FindProfileAsync(id, ct).ConfigureAwait(false);
        return profile is null ? null : MapProfile(profile);
    }

    public async Task<List<AiRunnerProfileDto>> GetProfileOptionsAsync(
        List<int> organizationIds, CancellationToken ct) =>
        (await repo.GetProfilesForOrganizationsAsync(organizationIds, ct).ConfigureAwait(false))
            .Select(MapProfile)
            .ToList();

    public async Task<AiRunnerProfileDto> CreateProfileAsync(
        CreateAiRunnerProfileRequest request, CancellationToken ct)
    {
        ValidateProfile(request.Binary, request.ArgsTemplate);
        var organizationId = request.OrganizationId
            ?? await organizations.GetDefaultOrganizationIdAsync(ct).ConfigureAwait(false)
            ?? throw new BadRequestException("No organization available.");
        var profile = new AiRunnerProfile
        {
            OrganizationId = organizationId,
            Name = request.Name.Trim(),
            Description = request.Description.Trim(),
            Binary = request.Binary.Trim(),
            ArgsTemplateJson = JsonSerializer.Serialize(request.ArgsTemplate),
            EnvironmentJsonEncrypted = encryption.EncryptValue(JsonSerializer.Serialize(request.Environment)),
            TimeoutSeconds = Math.Clamp(request.TimeoutSeconds, 1, 86_400),
            MaxOutputBytes = Math.Clamp(request.MaxOutputBytes, 1024, 1_000_000),
            SendsDataExternally = request.SendsDataExternally
        };
        await repo.AddProfileAsync(profile, ct).ConfigureAwait(false);
        await audit.LogAsync("Created", "AiRunnerProfile", profile.Id, profile.Name, ct)
            .ConfigureAwait(false);
        return MapProfile(profile);
    }

    public async Task<AiRunnerProfileDto?> UpdateProfileAsync(
        int id, UpdateAiRunnerProfileRequest request, CancellationToken ct)
    {
        ValidateProfile(request.Binary, request.ArgsTemplate);
        var profile = await repo.FindProfileAsync(id, ct).ConfigureAwait(false);
        if (profile is null) return null;
        profile.Name = request.Name.Trim();
        profile.Description = request.Description.Trim();
        profile.Binary = request.Binary.Trim();
        profile.ArgsTemplateJson = JsonSerializer.Serialize(request.ArgsTemplate);
        profile.EnvironmentJsonEncrypted = encryption.EncryptValue(JsonSerializer.Serialize(
            RestoreMaskedEnvironment(profile.EnvironmentJsonEncrypted, request.Environment)));
        profile.TimeoutSeconds = Math.Clamp(request.TimeoutSeconds, 1, 86_400);
        profile.MaxOutputBytes = Math.Clamp(request.MaxOutputBytes, 1024, 1_000_000);
        profile.SendsDataExternally = request.SendsDataExternally;
        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.LogAsync("Updated", "AiRunnerProfile", profile.Id, profile.Name, ct)
            .ConfigureAwait(false);
        return MapProfile(profile);
    }

    public async Task<bool> DeleteProfileAsync(int id, CancellationToken ct)
    {
        var profile = await repo.FindProfileAsync(id, ct).ConfigureAwait(false);
        if (profile is null) return false;
        if (await repo.ProfileHasDefinitionsAsync(id, ct).ConfigureAwait(false))
            throw new ConflictException("The AI runner profile is used by one or more task definitions.");
        await repo.RemoveProfileAsync(profile, ct).ConfigureAwait(false);
        await audit.LogAsync("Deleted", "AiRunnerProfile", id, profile.Name, ct)
            .ConfigureAwait(false);
        return true;
    }

    public async Task<PaginatedResult<AiTaskDefinitionDto>> GetDefinitionsAsync(
        PaginationRequest request, int? projectId, int? serverId,
        List<int>? accessibleProjectIds, List<int>? accessibleServerIds, CancellationToken ct)
    {
        var (page, pageSize) = request.Normalize();
        var (items, total) = await repo.GetDefinitionsPageAsync(
            request.Search, page, pageSize, projectId, serverId,
            accessibleProjectIds, accessibleServerIds, ct, request.Filters,
            request.SortBy, request.SortDescending).ConfigureAwait(false);
        return new PaginatedResult<AiTaskDefinitionDto>
        {
            Items = items.Select(MapDefinition).ToList(),
            TotalCount = total,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<AiTaskDefinitionDto?> GetDefinitionAsync(int id, CancellationToken ct)
    {
        var definition = await repo.FindDefinitionAsync(id, ct).ConfigureAwait(false);
        return definition is null ? null : MapDefinition(definition);
    }

    public async Task<AiTaskDefinitionDto> CreateDefinitionAsync(
        CreateAiTaskDefinitionRequest request, CancellationToken ct)
    {
        await ValidateDefinitionAsync(
            request.ProfileId, request.ProjectId, request.ServerId,
            request.Schedule, request.EventTypes, ct).ConfigureAwait(false);
        var definition = new AiTaskDefinition
        {
            Name = request.Name.Trim(),
            ProfileId = request.ProfileId,
            PromptTemplate = request.PromptTemplate,
            ProjectId = request.ProjectId,
            ServerId = request.ServerId,
            Schedule = NormalizeSchedule(request.Schedule),
            Enabled = request.Enabled,
            Triggers = BuildTriggers(request.EventTypes)
        };
        await repo.AddDefinitionAsync(definition, ct).ConfigureAwait(false);
        await audit.LogAsync("Created", "AiTaskDefinition", definition.Id, definition.Name, ct)
            .ConfigureAwait(false);
        return MapDefinition((await repo.FindDefinitionAsync(definition.Id, ct).ConfigureAwait(false))!);
    }

    public async Task<AiTaskDefinitionDto?> UpdateDefinitionAsync(
        int id, UpdateAiTaskDefinitionRequest request, CancellationToken ct)
    {
        await ValidateDefinitionAsync(
            request.ProfileId, request.ProjectId, request.ServerId,
            request.Schedule, request.EventTypes, ct).ConfigureAwait(false);
        var definition = await repo.FindDefinitionAsync(id, ct).ConfigureAwait(false);
        if (definition is null) return null;
        definition.Name = request.Name.Trim();
        definition.ProfileId = request.ProfileId;
        definition.PromptTemplate = request.PromptTemplate;
        definition.ProjectId = request.ProjectId;
        definition.ServerId = request.ServerId;
        definition.Schedule = NormalizeSchedule(request.Schedule);
        definition.Enabled = request.Enabled;
        definition.Triggers.Clear();
        definition.Triggers.AddRange(BuildTriggers(request.EventTypes));
        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.LogAsync("Updated", "AiTaskDefinition", definition.Id, definition.Name, ct)
            .ConfigureAwait(false);
        return MapDefinition(definition);
    }

    public async Task<bool> DeleteDefinitionAsync(int id, CancellationToken ct)
    {
        var definition = await repo.FindDefinitionAsync(id, ct).ConfigureAwait(false);
        if (definition is null) return false;
        await repo.RemoveDefinitionAsync(definition, ct).ConfigureAwait(false);
        await audit.LogAsync("Deleted", "AiTaskDefinition", id, definition.Name, ct)
            .ConfigureAwait(false);
        return true;
    }

    public async Task<ServerTask> RunNowAsync(
        int definitionId, string? eventPayload, CancellationToken ct)
    {
        var definition = await repo.FindDefinitionAsync(definitionId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException("AI task definition not found.");
        var server = await repo.ResolveExecutionServerAsync(definition, ct).ConfigureAwait(false)
            ?? throw new BadRequestException("No execution server is available for this AI task.");
        if (await repo.GetActiveRunCountAsync(server.Id, ct).ConfigureAwait(false)
            >= MaxConcurrentRunsPerServer)
            throw new ConflictException("The AI run concurrency limit has been reached on this server.");

        var prompt = string.IsNullOrWhiteSpace(eventPayload)
            ? definition.PromptTemplate
            : $"{definition.PromptTemplate}\n\nEvent payload:\n{eventPayload}";
        var spec = await BuildExecutionSpecAsync(
            definition.Profile, prompt, false, null, definition.Id, string.Empty, ct)
            .ConfigureAwait(false);
        if (definition.ProjectId is { } projectId)
        {
            var sourceRepository = await repo.GetPrimaryProjectRepositoryAsync(projectId, ct)
                .ConfigureAwait(false)
                ?? throw new BadRequestException(
                    "The project has no non-empty internal repository for the AI workspace.");
            spec.EnvironmentVariables["AETHEUS_AI_SOURCE_REPOSITORY_ID"] =
                sourceRepository.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            spec.EnvironmentVariables["AETHEUS_AI_SOURCE_REPOSITORY_PATH"] =
                $"/git/{projectId}/{sourceRepository.Slug}.git";
            spec.EnvironmentVariables["AETHEUS_AI_SOURCE_REF"] = sourceRepository.DefaultBranch;
            var (gitUsername, gitPassword) = GitAiCloneToken.Mint(
                configuration,
                definition.Id,
                projectId,
                timeProvider.GetUtcNow().UtcDateTime);
            spec.EnvironmentVariables["GIT_USERNAME"] = gitUsername;
            spec.EnvironmentVariables["GIT_PASSWORD"] = gitPassword;
        }
        var task = new ServerTask
        {
            ServerId = server.Id,
            Name = definition.Name,
            Command = spec.Target,
            Operation = OperationKind.AiRun,
            TimeoutSeconds = spec.TimeoutSeconds,
            EnvironmentVariables = TaskEnvProtection.Protect(
                encryption, JsonSerializer.Serialize(spec.EnvironmentVariables))
        };
        task = await repo.AddTaskAsync(task, ct).ConfigureAwait(false);
        await taskQueueNotifier.NotifyTaskQueuedAsync(task, ct: ct).ConfigureAwait(false);
        return task;
    }

    public async Task<AiExecutionSpec?> BuildPipelineExecutionAsync(
        string profileName, int organizationId, string prompt, bool gate,
        int pipelineRunId, string workingDirectory, CancellationToken ct)
    {
        var profile = await repo.FindProfileByNameAsync(profileName, organizationId, ct)
            .ConfigureAwait(false);
        if (profile is null) return null;
        return await BuildExecutionSpecAsync(
            profile, prompt, gate, pipelineRunId, null, workingDirectory, ct).ConfigureAwait(false);
    }

    public async Task<PaginatedResult<AiRunResultDto>> GetResultsAsync(
        int? definitionId, int? pipelineRunId, PaginationRequest request, CancellationToken ct)
    {
        var (page, pageSize) = request.Normalize();
        var (items, total) = await repo.GetResultsPageAsync(
            definitionId, pipelineRunId, page, pageSize, ct).ConfigureAwait(false);
        return new PaginatedResult<AiRunResultDto>
        {
            Items = items.Select(MapResult).ToList(),
            TotalCount = total,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<AiRunResultDto> PublishResultAsync(
        PublishAiRunResultRequest request, CancellationToken ct)
    {
        var existing = await repo.FindResultByTaskAsync(request.ServerTaskId, ct).ConfigureAwait(false);
        if (existing is not null) return MapResult(existing);
        var task = await repo.FindTaskAsync(request.ServerTaskId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException("AI server task not found.");
        if (task.Operation != OperationKind.AiRun)
            throw new BadRequestException("The server task is not an AI run.");

        int? definitionId = null;
        int? expectedSourceRepositoryId = null;
        var envJson = TaskEnvProtection.Unprotect(encryption, task.EnvironmentVariables);
        if (JsonSerializer.Deserialize<Dictionary<string, string>>(envJson) is { } environment
            )
        {
            if (environment.TryGetValue("AETHEUS_AI_TASK_DEFINITION_ID", out var definitionText)
                && int.TryParse(definitionText, out var parsedDefinitionId))
                definitionId = parsedDefinitionId;
            if (environment.TryGetValue("AETHEUS_AI_SOURCE_REPOSITORY_ID", out var repositoryText)
                && int.TryParse(repositoryText, out var parsedRepositoryId))
                expectedSourceRepositoryId = parsedRepositoryId;
        }
        if (expectedSourceRepositoryId != request.SourceRepositoryId)
            throw new BadRequestException("The AI result source repository does not match its task.");
        if (request.BaseCommitSha is not null
            && !CommitShaRegex().IsMatch(request.BaseCommitSha))
            throw new BadRequestException("The AI result base commit is invalid.");

        var report = await secretMasking.MaskAsync(
            request.ReportMarkdown, task.PipelineRunId, ct).ConfigureAwait(false);
        var diff = request.DiffPatch is null
            ? null
            : await secretMasking.MaskAsync(request.DiffPatch, task.PipelineRunId, ct).ConfigureAwait(false);
        var result = new AiRunResult
        {
            ServerTaskId = task.Id,
            PipelineRunId = task.PipelineRunId,
            AiTaskDefinitionId = definitionId,
            ProfileName = request.ProfileName,
            SendsDataExternally = request.SendsDataExternally,
            ReportMarkdown = report,
            Verdict = request.Verdict,
            DiffPatch = diff,
            DurationMs = request.DurationMs,
            Truncated = request.Truncated,
            Succeeded = request.Succeeded,
            SourceRepositoryId = request.SourceRepositoryId,
            BaseCommitSha = request.BaseCommitSha
        };
        await repo.AddResultAsync(result, ct).ConfigureAwait(false);
        return MapResult(result);
    }

    public async Task<AiRunResultDto?> GetResultAsync(int id, CancellationToken ct)
    {
        var result = await repo.FindResultAsync(id, ct).ConfigureAwait(false);
        return result is null ? null : MapResult(result);
    }

    public async Task<AiPatchApplicationDto> ApplyProposedPatchAsync(
        int resultId, CancellationToken ct)
    {
        var result = await repo.FindResultAsync(resultId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException("AI run result not found.");
        var projectId = result.AiTaskDefinition?.ProjectId
            ?? result.PipelineRun?.Pipeline?.ProjectId
            ?? throw new BadRequestException("This AI result is not linked to a project.");
        if (string.IsNullOrWhiteSpace(result.DiffPatch) || result.Truncated)
            throw new BadRequestException("The proposed patch is empty or truncated.");
        var existing = ExistingPatchApplication(result);
        if (existing is not null) return existing;

        if (result.SourceRepositoryId is not { } sourceRepositoryId
            || string.IsNullOrWhiteSpace(result.BaseCommitSha))
            throw new BadRequestException("The AI result has no verified source provenance.");
        var repository = await gitLightService.GetRepositoryAsync(sourceRepositoryId, ct)
            .ConfigureAwait(false)
            ?? throw new BadRequestException("The source repository no longer exists.");
        if (repository.ProjectId != projectId)
            throw new BadRequestException("The AI result source repository belongs to another project.");
        var branchName = $"ai-proposed/{result.Id}";
        await gitLightService.CreateBranchAsync(
            repository.Id,
            new CreateGitLightBranchRequest
            {
                Name = branchName,
                StartRef = result.BaseCommitSha
            },
            ct).ConfigureAwait(false);
        var applied = await gitLightService.ApplyPatchAsync(
            repository.Id, branchName, result.DiffPatch, ct).ConfigureAwait(false);
        if (!applied.Success || string.IsNullOrWhiteSpace(applied.CommitSha))
            throw new ConflictException(applied.Error ?? "The proposed patch could not be applied.");

        result.ProposedRepositoryId = repository.Id;
        result.ProposedBranchName = branchName;
        result.ProposedCommitSha = applied.CommitSha;
        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.LogAsync(
            "AppliedProposedPatch", "AiRunResult", result.Id, branchName, ct).ConfigureAwait(false);
        return PatchApplication(repository.Id, branchName, applied.CommitSha);
    }

    private static AiPatchApplicationDto? ExistingPatchApplication(AiRunResult result) =>
        result.ProposedRepositoryId is { } repositoryId
        && !string.IsNullOrWhiteSpace(result.ProposedBranchName)
        && !string.IsNullOrWhiteSpace(result.ProposedCommitSha)
            ? PatchApplication(repositoryId, result.ProposedBranchName, result.ProposedCommitSha)
            : null;

    private static AiPatchApplicationDto PatchApplication(
        int repositoryId, string branchName, string commitSha) => new()
        {
            RepositoryId = repositoryId,
            BranchName = branchName,
            CommitSha = commitSha
        };

    public async Task<AiConsumptionDto> GetConsumptionAsync(int? projectId, CancellationToken ct)
    {
        var since = timeProvider.GetUtcNow().UtcDateTime.AddDays(-7);
        var profiles = await repo.GetConsumptionByProfileAsync(since, projectId, ct)
            .ConfigureAwait(false);
        return new AiConsumptionDto
        {
            RunCount = profiles.Sum(profile => profile.RunCount),
            FailedCount = profiles.Sum(profile => profile.FailedCount),
            DurationMs = profiles.Sum(profile => profile.DurationMs),
            Profiles = profiles
        };
    }

    public Task<int?> GetRunPipelineIdAsync(int pipelineRunId, CancellationToken ct) =>
        repo.GetRunPipelineIdAsync(pipelineRunId, ct);

    public async Task DispatchEventAsync(string eventType, object payload, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var json = JsonSerializer.Serialize(payload);
        // BACK-003: only the definitions of the project or server the event is about.
        var owner = AiEventOwner.Read(json);
        if (!owner.IsKnown) return;
        foreach (var definition in await repo.GetDefinitionsForEventAsync(
                     eventType, owner.ProjectId, owner.ServerId, ct).ConfigureAwait(false))
        {
            var trigger = definition.Triggers.First(item => item.EventType == eventType);
            if (trigger.LastTriggeredAt is { } last && now - last < EventCooldown)
            {
                logger.LogInformation(
                    "AI task {DefinitionId} event {EventType} suppressed by cooldown",
                    definition.Id, eventType);
                continue;
            }

            try
            {
                await RunNowAsync(definition.Id, json, ct).ConfigureAwait(false);
                trigger.LastTriggeredAt = now;
                await repo.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            catch (ConflictException ex)
            {
                logger.LogWarning(
                    ex, "AI task {DefinitionId} event {EventType} suppressed by concurrency limit",
                    definition.Id, eventType);
            }
        }
    }

    private async Task<AiExecutionSpec> BuildExecutionSpecAsync(
        AiRunnerProfile profile, string prompt, bool gate, int? pipelineRunId,
        int? definitionId, string workingDirectory, CancellationToken ct)
    {
        var maskedPrompt = await secretMasking.MaskAsync(prompt, pipelineRunId, ct).ConfigureAwait(false);
        var environment = JsonSerializer.Deserialize<Dictionary<string, string>>(
            encryption.DecryptValue(profile.EnvironmentJsonEncrypted)) ?? [];
        environment["AETHEUS_AI_PROFILE_NAME"] = profile.Name;
        environment["AETHEUS_AI_SENDS_DATA_EXTERNALLY"] =
            profile.SendsDataExternally ? "true" : "false";
        environment["AETHEUS_AI_BINARY"] = profile.Binary;
        environment["AETHEUS_AI_ARGS_JSON"] = profile.ArgsTemplateJson;
        environment["AETHEUS_AI_PROMPT"] = maskedPrompt;
        environment["AETHEUS_AI_GATE"] = gate ? "true" : "false";
        environment["AETHEUS_AI_MAX_OUTPUT_BYTES"] = profile.MaxOutputBytes.ToString();
        environment["AETHEUS_AI_WORKING_DIR"] = workingDirectory;
        if (definitionId.HasValue)
            environment["AETHEUS_AI_TASK_DEFINITION_ID"] = definitionId.Value.ToString();
        return new AiExecutionSpec("ai-run", profile.TimeoutSeconds, environment);
    }

    private async Task ValidateDefinitionAsync(
        int profileId, int? projectId, int? serverId, string? schedule,
        List<string> eventTypes, CancellationToken ct)
    {
        if (projectId.HasValue == serverId.HasValue)
            throw new BadRequestException("Exactly one owner must be set: ProjectId or ServerId.");
        var profile = await repo.FindProfileAsync(profileId, ct).ConfigureAwait(false)
            ?? throw new BadRequestException("AI runner profile not found.");
        var ownerOrganizationId = projectId.HasValue
            ? await repo.GetProjectOrganizationIdAsync(projectId.Value, ct).ConfigureAwait(false)
            : await repo.GetServerOrganizationIdAsync(serverId!.Value, ct).ConfigureAwait(false);
        if (ownerOrganizationId != profile.OrganizationId)
            throw new BadRequestException("The AI profile and task owner must belong to the same organization.");
        _ = ParseSchedule(schedule);
        if (eventTypes.Count > MaxEventTypes || eventTypes.Any(type => !EventTypeRegex().IsMatch(type)))
            throw new BadRequestException("AI event types are invalid or exceed the allowed count.");
    }

    private static void ValidateProfile(string binary, List<string> arguments)
    {
        if (!BinaryRegex().IsMatch(binary)
            || binary.Contains("..", StringComparison.Ordinal)
            || arguments.Count > MaxArguments)
            throw new BadRequestException("The AI runner binary or argument template is invalid.");
        foreach (var argument in arguments)
        {
            var braces = argument.Contains('{') || argument.Contains('}');
            if (braces && argument is not ("{prompt_file}" or "{workdir}" or "{output_file}"))
                throw new BadRequestException("AI placeholders must occupy a complete argument token.");
        }
        if (!arguments.Contains("{prompt_file}", StringComparer.Ordinal))
            throw new BadRequestException("The AI runner arguments must include {prompt_file}.");
    }

    private Dictionary<string, string> RestoreMaskedEnvironment(
        string encryptedExisting, Dictionary<string, string> requested)
    {
        var existing = JsonSerializer.Deserialize<Dictionary<string, string>>(
            encryption.DecryptValue(encryptedExisting)) ?? [];
        return requested.ToDictionary(
            item => item.Key,
            item => item.Value == "***" && existing.TryGetValue(item.Key, out var value)
                ? value
                : item.Value,
            StringComparer.Ordinal);
    }

    private AiRunnerProfileDto MapProfile(AiRunnerProfile profile)
    {
        var environment = JsonSerializer.Deserialize<Dictionary<string, string>>(
            encryption.DecryptValue(profile.EnvironmentJsonEncrypted)) ?? [];
        return new AiRunnerProfileDto
        {
            Id = profile.Id,
            OrganizationId = profile.OrganizationId,
            Name = profile.Name,
            Description = profile.Description,
            Binary = profile.Binary,
            ArgsTemplate = JsonSerializer.Deserialize<List<string>>(profile.ArgsTemplateJson) ?? [],
            Environment = environment.Keys.ToDictionary(key => key, _ => "***"),
            TimeoutSeconds = profile.TimeoutSeconds,
            MaxOutputBytes = profile.MaxOutputBytes,
            SendsDataExternally = profile.SendsDataExternally,
            CreatedAt = profile.CreatedAt,
            UpdatedAt = profile.UpdatedAt
        };
    }

    private static AiTaskDefinitionDto MapDefinition(AiTaskDefinition definition) => new()
    {
        Id = definition.Id,
        Name = definition.Name,
        ProfileId = definition.ProfileId,
        ProfileName = definition.Profile?.Name ?? string.Empty,
        ProfileSendsDataExternally = definition.Profile?.SendsDataExternally ?? false,
        PromptTemplate = definition.PromptTemplate,
        ProjectId = definition.ProjectId,
        ProjectName = definition.Project?.Name,
        ServerId = definition.ServerId,
        ServerName = definition.Server?.Name,
        Schedule = definition.Schedule,
        Enabled = definition.Enabled,
        EventTypes = definition.Triggers.Select(trigger => trigger.EventType).Order().ToList(),
        LastScheduledAt = definition.LastScheduledAt,
        CreatedAt = definition.CreatedAt,
        UpdatedAt = definition.UpdatedAt
    };

    private static AiRunResultDto MapResult(AiRunResult result) => new()
    {
        Id = result.Id,
        ServerTaskId = result.ServerTaskId,
        PipelineRunId = result.PipelineRunId,
        AiTaskDefinitionId = result.AiTaskDefinitionId,
        ProfileName = result.ProfileName,
        SendsDataExternally = result.SendsDataExternally,
        ReportMarkdown = result.ReportMarkdown,
        Verdict = result.Verdict,
        DiffPatch = result.DiffPatch,
        DurationMs = result.DurationMs,
        Truncated = result.Truncated,
        Succeeded = result.Succeeded,
        ProjectId = result.AiTaskDefinition?.ProjectId ?? result.PipelineRun?.Pipeline?.ProjectId,
        SourceRepositoryId = result.SourceRepositoryId,
        BaseCommitSha = result.BaseCommitSha,
        ProposedRepositoryId = result.ProposedRepositoryId,
        ProposedBranchName = result.ProposedBranchName,
        ProposedCommitSha = result.ProposedCommitSha,
        CreatedAt = result.CreatedAt
    };

    private static List<AiTaskTrigger> BuildTriggers(IEnumerable<string> eventTypes) =>
        eventTypes.Where(type => !string.IsNullOrWhiteSpace(type))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(type => new AiTaskTrigger { EventType = type.Trim().ToLowerInvariant() })
        .ToList();

    [GeneratedRegex("^[0-9a-fA-F]{40}([0-9a-fA-F]{24})?$", RegexOptions.CultureInvariant)]
    private static partial Regex CommitShaRegex();

    private static string? NormalizeSchedule(string? schedule) =>
        string.IsNullOrWhiteSpace(schedule) ? null : schedule.Trim();

    internal static CronExpression? ParseSchedule(string? schedule) =>
        string.IsNullOrWhiteSpace(schedule)
            ? null
            : CronExpression.Parse(schedule, CronFormat.IncludeSeconds);

    [GeneratedRegex(@"^[A-Za-z0-9._:\\/ -]{1,260}$", RegexOptions.CultureInvariant)]
    private static partial Regex BinaryRegex();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]{0,119}$", RegexOptions.CultureInvariant)]
    private static partial Regex EventTypeRegex();
}
