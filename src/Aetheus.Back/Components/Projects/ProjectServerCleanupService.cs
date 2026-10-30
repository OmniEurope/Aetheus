// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Projects;

/// <summary>Recette R-97 (ADR-058): finds and removes, through the servers' agents, what a project deployed,
/// before its Aetheus data is deleted.</summary>
public interface IProjectServerCleanupService
{
    /// <summary>The servers and refused values of the project. Null when the project does not exist.</summary>
    Task<ProjectServerCleanupPlan?> GetPlanAsync(int projectId, CancellationToken ct = default);

    /// <summary>Queues one discovery per server Aetheus can ask.</summary>
    Task<List<ProjectServerResourceTaskDto>> QueueDiscoveryAsync(int projectId, CancellationToken ct = default);

    /// <summary>The project's server tasks among <paramref name="taskIds"/>, as far as they went; tasks of
    /// another project or another kind are left out.</summary>
    Task<List<ProjectServerResourceTaskDto>> GetTasksAsync(int projectId, IReadOnlyCollection<int> taskIds, CancellationToken ct = default);

    /// <summary>Queues, on each server, the removal of exactly what its discovery found.</summary>
    Task<List<ProjectServerResourceTaskDto>> QueueRemovalAsync(int projectId, ProjectServerCleanupRequest request, CancellationToken ct = default);

    /// <summary>Whether the given tasks prove that nothing the project deployed is left. Null when the
    /// project does not exist.</summary>
    Task<ProjectServerCleanupVerdict?> VerifyAsync(int projectId, IReadOnlyCollection<int> taskIds, CancellationToken ct = default);
}

/// <summary>The backend never builds a command from a library value: it validates each value against the
/// strict names of <see cref="ProjectServerResourceNames"/>, sends the identity as a typed operation whose target
/// is the project id, and builds the removal list only from a discovery the agent of the same server printed for
/// this project, filtered again by the current identity. A task is the project's only when its operation, its
/// target (the project id) and its server (one of the project's, in the project's organization) all match.</summary>
public sealed class ProjectServerCleanupService(
    IProjectServerCleanupRepository repo,
    IProjectDeletionRepository deletion,
    ITaskService taskService,
    IProjectServerCleanupGuardSource guards,
    IAuditService audit,
    TimeProvider time) : IProjectServerCleanupService
{
    internal const int DiscoveryTimeoutSeconds = 180;
    internal const int RemovalTimeoutSeconds = 900;

    /// <summary>How long a discovery stays a valid basis for the removal and the verdict. The dialog follows each
    /// phase for a few minutes at most; an hour leaves the user time on the confirmation step while a preview left
    /// open overnight, or read before a later deployment, is never taken as proof that nothing is left.</summary>
    internal static readonly TimeSpan DiscoveryValidity = TimeSpan.FromHours(1);

    public async Task<ProjectServerCleanupPlan?> GetPlanAsync(int projectId, CancellationToken ct = default)
    {
        var inputs = await repo.LoadInputsAsync(projectId, ct).ConfigureAwait(false);
        if (inputs is null) return null;
        return ProjectServerCleanupPlanner.Build(inputs, await guards.GetAsync(ct).ConfigureAwait(false));
    }

    public async Task<List<ProjectServerResourceTaskDto>> QueueDiscoveryAsync(int projectId, CancellationToken ct = default)
    {
        var plan = await RequirePlanAsync(projectId, ct).ConfigureAwait(false);
        var queued = new List<ProjectServerResourceTaskDto>();
        foreach (var server in plan.Servers.Where(item => item.State == ProjectDeletionServerState.Ready))
        {
            var task = Operation(server, plan, OperationKind.ProjectResourcesDiscover,
                $"Project deletion - find the resources of {plan.ProjectName}", server.Spec(), DiscoveryTimeoutSeconds);
            await TaskQueuePersistence.PersistAndNotifyAsync(repo.AddTaskAsync, taskService, task, ct).ConfigureAwait(false);
            queued.Add(Queued(task, server, ProjectServerTaskPhase.Discovery));
        }
        return queued;
    }

    public async Task<List<ProjectServerResourceTaskDto>> GetTasksAsync(
        int projectId, IReadOnlyCollection<int> taskIds, CancellationToken ct = default)
    {
        // Polled every second or two by the deletion dialog: the plan is built from the light read (see
        // IProjectServerCleanupRepository.LoadTaskInputsAsync), only to place the tasks on the project's servers.
        var inputs = await repo.LoadTaskInputsAsync(projectId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"Project {projectId} not found.");
        var plan = ProjectServerCleanupPlanner.Build(inputs, await guards.GetAsync(ct).ConfigureAwait(false));
        var reports = await ReportsAsync(plan, taskIds, ct).ConfigureAwait(false);
        return [.. reports.Select(report => ProjectServerCleanupReports.ToDto(report, plan.Server(report.Task.ServerId)))];
    }

    public async Task<List<ProjectServerResourceTaskDto>> QueueRemovalAsync(
        int projectId, ProjectServerCleanupRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var plan = await RequirePlanAsync(projectId, ct).ConfigureAwait(false);
        var scope = await deletion.ResolveScopeAsync(projectId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"Project {projectId} not found.");
        var activeRuns = await deletion.CountActiveRunsAsync(scope, ct).ConfigureAwait(false);
        if (activeRuns > 0)
            throw new ConflictException(string.Create(CultureInfo.InvariantCulture,
                $"Project '{plan.ProjectName}' has {activeRuns} pipeline run(s) in progress: nothing is removed from its servers until they end."));

        var ids = request.DiscoveryTaskIds.Distinct().ToList();
        var reports = await ReportsAsync(plan, ids, ct).ConfigureAwait(false);
        var foreign = ids.Except(reports.Where(item => item.Phase == ProjectServerTaskPhase.Discovery).Select(item => item.Task.Id)).ToList();
        if (foreign.Count > 0)
            throw new BadRequestException($"Task(s) {string.Join(", ", foreign)} are not discoveries of this project's servers.");

        var isRecent = await RecentDiscoveryRuleAsync(plan, ct).ConfigureAwait(false);
        var removals = new List<(ProjectServerCleanupTarget Server, List<ProjectServerResourceRef> Items)>();
        foreach (var discovery in reports.Where(item => item.Readable && isRecent(item)).GroupBy(item => item.Task.ServerId).Select(group => group.MaxBy(item => item.Task.Id)!))
        {
            var server = plan.Server(discovery.Task.ServerId);
            if (server is not { State: ProjectDeletionServerState.Ready }) continue;
            var spec = server.Spec();
            // What the agent cannot remove (no apache.manage or certbot.manage) is not asked: the verdict reports it.
            var items = discovery.Found.Where(item => spec.Covers(item) && server.CanRemove(item.Kind)).Distinct().ToList();
            if (items.Count > 0) removals.Add((server, items));
        }
        if (removals.Any(item => item.Items.Any(resource => resource.Kind == ProjectServerResourceKind.Volume)) && !request.AcknowledgeVolumeDataLoss)
            throw new BadRequestException("Docker volumes hold the project's data (its database): confirm that their data will be lost.");

        var queued = new List<ProjectServerResourceTaskDto>();
        foreach (var (server, items) in removals)
        {
            var task = Operation(server, plan, OperationKind.ProjectResourcesRemove,
                $"Project deletion - remove the resources of {plan.ProjectName}", server.Spec(items), RemovalTimeoutSeconds);
            await TaskQueuePersistence.PersistAndNotifyAsync(repo.AddTaskAsync, taskService, task, ct).ConfigureAwait(false);
            queued.Add(Queued(task, server, ProjectServerTaskPhase.Removal));
        }
        await audit.LogAsync("ServerResourcesRemovalQueued", "Project", projectId, string.Join("; ", removals.Select(item =>
            string.Create(CultureInfo.InvariantCulture, $"{item.Server.ServerName}: {item.Items.Count} resource(s)"))), ct).ConfigureAwait(false);
        return queued;
    }

    public async Task<ProjectServerCleanupVerdict?> VerifyAsync(int projectId, IReadOnlyCollection<int> taskIds, CancellationToken ct = default)
    {
        var plan = await GetPlanAsync(projectId, ct).ConfigureAwait(false);
        if (plan is null) return null;
        var reports = await ReportsAsync(plan, taskIds, ct).ConfigureAwait(false);
        var isRecent = await RecentDiscoveryRuleAsync(plan, ct).ConfigureAwait(false);
        return ProjectServerCleanupReports.Verify(plan, reports, isRecent);
    }

    /// <summary>A discovery counts only when it was created after the project's latest pipeline task on that server
    /// (what a later deployment created was never read) and within <see cref="DiscoveryValidity"/>.</summary>
    private async Task<Func<ProjectServerTaskReport, bool>> RecentDiscoveryRuleAsync(ProjectServerCleanupPlan plan, CancellationToken ct)
    {
        var notBefore = time.GetUtcNow().UtcDateTime - DiscoveryValidity;
        var lastActivity = await repo.GetLastProjectActivityAsync(
            plan.ProjectId, [.. plan.Servers.Select(item => item.ServerId)], ct).ConfigureAwait(false);
        return report => report.Task.CreatedAt >= notBefore
            && (!lastActivity.TryGetValue(report.Task.ServerId, out var last) || report.Task.CreatedAt > last);
    }

    private async Task<ProjectServerCleanupPlan> RequirePlanAsync(int projectId, CancellationToken ct) =>
        await GetPlanAsync(projectId, ct).ConfigureAwait(false) ?? throw new NotFoundException($"Project {projectId} not found.");

    /// <summary>The tasks that are this project's: right operation, the project id as target, and a server of
    /// the plan (so of the project's organization).</summary>
    private async Task<List<ProjectServerTaskReport>> ReportsAsync(
        ProjectServerCleanupPlan plan, IReadOnlyCollection<int> taskIds, CancellationToken ct)
    {
        if (taskIds.Count == 0) return [];
        if (taskIds.Count > ProjectServerResourceLimits.MaxTaskIds)
            throw new BadRequestException($"At most {ProjectServerResourceLimits.MaxTaskIds} tasks can be read at once.");
        var tasks = await repo.GetProjectTasksAsync(plan.ProjectId, taskIds, ct).ConfigureAwait(false);
        var own = tasks.Where(item => plan.Server(item.ServerId) is not null).ToList();
        if (own.Count == 0) return [];
        var outputs = await repo.GetTaskOutputsAsync([.. own.Select(item => item.Id)], ct).ConfigureAwait(false);
        return [.. own.Select(task =>
        {
            var output = outputs.GetValueOrDefault(task.Id, string.Empty);
            return new ProjectServerTaskReport(task, ProjectServerResourceOutput.Parse(output), output);
        })];
    }

    private static ServerTask Operation(ProjectServerCleanupTarget server, ProjectServerCleanupPlan plan, OperationKind kind,
        string name, ProjectServerResourceSpec spec, int timeoutSeconds)
    {
        var refusal = spec.Validate();
        if (refusal is not null) throw new BadRequestException(refusal);
        return ServerTaskFactory.Operation(server.ServerId, name, kind, plan.ProjectId.ToString(CultureInfo.InvariantCulture),
            new Dictionary<string, string> { [ProjectServerResourceSpec.EnvironmentVariable] = spec.Encode() }, timeoutSeconds);
    }

    private static ProjectServerResourceTaskDto Queued(ServerTask task, ProjectServerCleanupTarget server, ProjectServerTaskPhase phase) => new()
    {
        TaskId = task.Id,
        ServerId = server.ServerId,
        ServerName = server.ServerName,
        Phase = phase,
        State = ProjectServerTaskState.Running,
    };
}
