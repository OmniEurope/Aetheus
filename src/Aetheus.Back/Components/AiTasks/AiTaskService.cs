// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using System.Text.RegularExpressions;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data.Entities;
using Cronos;

namespace Aetheus.Back.Components.AiTasks;

public sealed partial class AiTaskService(
    IAiTaskRepository repo,
    IAuditService audit,
    IEncryptionService encryption,
    ISecretMaskingService secretMasking,
    ITaskQueueNotifier taskQueueNotifier,
    TimeProvider timeProvider,
    IConfiguration configuration,
    ILogger<AiTaskService> logger,
    Notifications.IUserNotificationService? userNotifications = null) : IAiTaskService
{
    private const int MaxEventTypes = 16;
    private const int MaxConcurrentRunsPerServer = 2;
    private static readonly TimeSpan EventCooldown = TimeSpan.FromMinutes(10);

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
        var (contextPathsJson, gitRange) = NormalizeRepositoryContext(request);
        var definition = new AiTaskDefinition
        {
            Name = request.Name.Trim(),
            ProfileId = request.ProfileId,
            PromptTemplate = request.PromptTemplate,
            ProjectId = request.ProjectId,
            ServerId = request.ServerId,
            Schedule = NormalizeSchedule(request.Schedule),
            Enabled = request.Enabled,
            ContextPathsJson = contextPathsJson,
            GitRange = gitRange,
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
        var (contextPathsJson, gitRange) = NormalizeRepositoryContext(request);
        var definition = await repo.FindDefinitionAsync(id, ct).ConfigureAwait(false);
        if (definition is null) return null;
        definition.Name = request.Name.Trim();
        definition.ProfileId = request.ProfileId;
        definition.PromptTemplate = request.PromptTemplate;
        definition.ProjectId = request.ProjectId;
        definition.ServerId = request.ServerId;
        definition.Schedule = NormalizeSchedule(request.Schedule);
        definition.Enabled = request.Enabled;
        definition.ContextPathsJson = contextPathsJson;
        definition.GitRange = gitRange;
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
        if (AiProfileRuntime.Problem(definition.Profile) is { } problem)
            throw new BadRequestException(problem);
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
            AddRepositoryContext(spec.EnvironmentVariables, definition);
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

    public async Task<AiPipelineExecution> BuildPipelineExecutionAsync(
        string? profileName, int? pipelineProfileId, int organizationId, string prompt, bool gate,
        int pipelineRunId, string workingDirectory, CancellationToken ct)
    {
        var selection = await SelectPipelineProfileAsync(profileName, pipelineProfileId, organizationId, ct)
            .ConfigureAwait(false);
        if (selection.Profile is not { } profile)
            return new AiPipelineExecution(null, selection.Problem, selection.Reason, true);
        // The key is a secret of this run from now on: anything the run shows a person is masked.
        secretMasking.RegisterRuntimeSecret(pipelineRunId, AiProfileRuntime.ApiKey(profile, encryption));
        var spec = await BuildExecutionSpecAsync(
            profile, prompt, gate, pipelineRunId, null, workingDirectory, ct).ConfigureAwait(false);
        // The agent writes this line at the top of the step log, so the profile used and why are never silent.
        spec.EnvironmentVariables["AETHEUS_AI_PROFILE_RESOLUTION"] = selection.Reason;
        return new AiPipelineExecution(spec, null, selection.Reason, selection.IsFallback);
    }

    public async Task<string?> FindPipelineProfileProblemAsync(
        string? profileName, int organizationId, CancellationToken ct) =>
        (await SelectPipelineProfileAsync(profileName, null, organizationId, ct).ConfigureAwait(false)).Problem;

    private async Task<AiProfileSelection> SelectPipelineProfileAsync(
        string? profileName, int? pipelineProfileId, int organizationId, CancellationToken ct) =>
        AiProfileResolver.Resolve(
            profileName, pipelineProfileId,
            await repo.GetOrganizationProfilesByPriorityAsync(organizationId, ct).ConfigureAwait(false));

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
        string? apiKey = null;
        var envJson = TaskEnvProtection.Unprotect(encryption, task.EnvironmentVariables);
        if (JsonSerializer.Deserialize<Dictionary<string, string>>(envJson) is { } environment)
        {
            if (environment.TryGetValue("AETHEUS_AI_TASK_DEFINITION_ID", out var definitionText)
                && int.TryParse(definitionText, out var parsedDefinitionId))
                definitionId = parsedDefinitionId;
            if (environment.TryGetValue("AETHEUS_AI_SOURCE_REPOSITORY_ID", out var repositoryText)
                && int.TryParse(repositoryText, out var parsedRepositoryId))
                expectedSourceRepositoryId = parsedRepositoryId;
            apiKey = environment.GetValueOrDefault("AETHEUS_AI_API_KEY");
        }
        if (expectedSourceRepositoryId != request.SourceRepositoryId)
            throw new BadRequestException("The AI result source repository does not match its task.");
        if (request.BaseCommitSha is not null
            && !CommitShaRegex().IsMatch(request.BaseCommitSha))
            throw new BadRequestException("The AI result base commit is invalid.");

        // The agent already masks the key; a standalone task has no run secrets, so the backend masks
        // its own copy again before storing what a person will read.
        var report = AiProviderClient.Mask(
            await secretMasking.MaskAsync(request.ReportMarkdown, task.PipelineRunId, ct).ConfigureAwait(false),
            apiKey);
        var result = new AiRunResult
        {
            ServerTaskId = task.Id,
            PipelineRunId = task.PipelineRunId,
            AiTaskDefinitionId = definitionId,
            ProfileName = request.ProfileName,
            SendsDataExternally = request.SendsDataExternally,
            ReportMarkdown = report,
            Verdict = request.Verdict,
            DurationMs = request.DurationMs,
            Truncated = request.Truncated,
            Succeeded = request.Succeeded,
            SourceRepositoryId = request.SourceRepositoryId,
            BaseCommitSha = request.BaseCommitSha
        };
        await repo.AddResultAsync(result, ct).ConfigureAwait(false);
        await NotifyRunCompletedAsync(result, ct).ConfigureAwait(false);
        return MapResult(result);
    }

    /// <summary>
    /// Recette R3-16: a finished run of an AI task tells the subscribers of its project (or of the projects
    /// its server is attached to), with what the notification needs to link to the run on the task's results
    /// page. A run of a pipeline step has no task and notifies nothing here. Never fails the publication.
    /// </summary>
    private async Task NotifyRunCompletedAsync(AiRunResult result, CancellationToken ct)
    {
        if (userNotifications is null || result.AiTaskDefinitionId is not { } definitionId) return;
        try
        {
            var definition = await repo.FindDefinitionAsync(definitionId, ct).ConfigureAwait(false);
            if (definition is null) return;
            object Payload(int projectId) => new
            {
                ProjectId = projectId,
                AiTaskDefinitionId = definition.Id,
                AiRunResultId = result.Id,
                TaskName = definition.Name,
                result.Succeeded,
                Verdict = result.Verdict.ToString()
            };
            if (definition.ProjectId is { } ownerProjectId)
                await userNotifications.RecordProjectEventAsync(
                    NotificationEventTypes.AiTaskCompleted, JsonSerializer.Serialize(Payload(ownerProjectId)), ct)
                    .ConfigureAwait(false);
            else if (definition.ServerId is { } serverId)
                await userNotifications.RecordServerEventAsync(
                    NotificationEventTypes.AiTaskCompleted, serverId, Payload, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Mandatory: the result is stored; a notification that cannot be written must not undo that.
            logger.LogWarning(ex, "[AiTasks] Run {ResultId} stored, but its notification failed", result.Id);
        }
    }

    public async Task<AiRunResultDto?> GetResultAsync(int id, CancellationToken ct)
    {
        var result = await repo.FindResultAsync(id, ct).ConfigureAwait(false);
        return result is null ? null : MapResult(result);
    }

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
        var environment = AiProfileRuntime.Environment(profile, encryption);
        environment["AETHEUS_AI_PROMPT"] = maskedPrompt;
        environment["AETHEUS_AI_GATE"] = gate ? "true" : "false";
        environment["AETHEUS_AI_WORKING_DIR"] = workingDirectory;
        if (definitionId.HasValue)
            environment["AETHEUS_AI_TASK_DEFINITION_ID"] = definitionId.Value.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
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

    /// <summary>The repository content a project task attaches to its prompt, validated by the rule of
    /// the pipeline step keys <c>context_paths</c> and <c>git_range</c>. Blank entries are dropped.</summary>
    private static (string? ContextPathsJson, string? GitRange) NormalizeRepositoryContext(
        AiTaskDefinitionRequest request)
    {
        var paths = request.ContextPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var gitRange = string.IsNullOrWhiteSpace(request.GitRange) ? null : request.GitRange.Trim();
        if (paths.Count == 0 && gitRange is null) return (null, null);
        if (!request.ProjectId.HasValue)
            throw new BadRequestException(
                "Context paths and a git range need a project task: only a project task works on a repository.");
        if (!AiRepositoryContextRules.AreValidContextPaths(paths))
            throw new BadRequestException(
                $"AI context paths must be at most {AiRepositoryContextRules.MaxContextPaths} relative paths inside the repository, without '.' or '..'.");
        if (gitRange is not null
            && (!AiRepositoryContextRules.IsValidGitRange(gitRange) || !AiRepositoryContextRules.IsCommitRange(gitRange)))
            throw new BadRequestException("The AI git range must be a commit range of the form A..B, for example HEAD~10..HEAD.");
        return (paths.Count == 0 ? null : JsonSerializer.Serialize(paths), gitRange);
    }

    /// <summary>The same variables a pipeline <c>type: ai</c> step sets, read by the agent's prompt
    /// assembler: the agent code is shared, not duplicated.</summary>
    private static void AddRepositoryContext(Dictionary<string, string> environment, AiTaskDefinition definition)
    {
        var paths = ReadContextPaths(definition.ContextPathsJson);
        if (paths.Count > 0)
            environment["AETHEUS_AI_CONTEXT_PATHS_JSON"] = JsonSerializer.Serialize(paths);
        if (!string.IsNullOrWhiteSpace(definition.GitRange))
            environment["AETHEUS_AI_GIT_RANGE"] = definition.GitRange.Trim();
    }

    private static List<string> ReadContextPaths(string? json) =>
        string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<string>>(json) ?? [];

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
        ContextPaths = ReadContextPaths(definition.ContextPathsJson),
        GitRange = definition.GitRange,
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

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]{0,119}$", RegexOptions.CultureInvariant)]
    private static partial Regex EventTypeRegex();
}
