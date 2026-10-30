// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Components.Projects;

/// <summary>Reads what the server cleanup of a project deletion is planned from, and its tasks.</summary>
public interface IProjectServerCleanupRepository
{
    /// <summary>Null when the project does not exist.</summary>
    Task<ProjectServerCleanupInputs?> LoadInputsAsync(int projectId, CancellationToken ct = default);

    Task AddTaskAsync(ServerTask task, CancellationToken ct = default);

    /// <summary>The project resources tasks among <paramref name="taskIds"/> that act for this project.</summary>
    Task<List<ServerTask>> GetProjectTasksAsync(int projectId, IReadOnlyCollection<int> taskIds, CancellationToken ct = default);

    /// <summary>When the project last ran something on each of <paramref name="serverIds"/> (the creation of its
    /// latest pipeline task there); a server it never ran on is absent.</summary>
    Task<IReadOnlyDictionary<int, DateTime>> GetLastProjectActivityAsync(int projectId, IReadOnlyCollection<int> serverIds, CancellationToken ct = default);

    /// <summary>What the task list of a deletion needs to place its tasks: the project's own libraries, environments,
    /// project servers, backup policies and the production <c>deployServer</c> of its pipelines. The libraries of the
    /// organization's other projects (what they claim) are not read: they only add refusals, so the task list keeps
    /// every server the full plan has, and this is read on every poll. Null when the project does not exist.</summary>
    Task<ProjectServerCleanupInputs?> LoadTaskInputsAsync(int projectId, CancellationToken ct = default);

    /// <summary>The log lines of each task, in order, one per line, read in a single query; a task with no log
    /// line has an empty output.</summary>
    Task<IReadOnlyDictionary<int, string>> GetTaskOutputsAsync(IReadOnlyCollection<int> taskIds, CancellationToken ct = default);
}

internal sealed class ProjectServerCleanupRepository(AppDbContext db) : IProjectServerCleanupRepository
{
    public Task<ProjectServerCleanupInputs?> LoadInputsAsync(int projectId, CancellationToken ct = default) =>
        LoadAsync(projectId, includeOtherProjects: true, ct);

    public Task<ProjectServerCleanupInputs?> LoadTaskInputsAsync(int projectId, CancellationToken ct = default) =>
        LoadAsync(projectId, includeOtherProjects: false, ct);

    private async Task<ProjectServerCleanupInputs?> LoadAsync(int projectId, bool includeOtherProjects, CancellationToken ct)
    {
        var project = await db.Projects.AsNoTracking()
            .Where(item => item.Id == projectId)
            .Select(item => new { item.Name, item.OrganizationId })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (project is null) return null;

        var environmentIds = await db.Environments.AsNoTracking().Where(item => item.ProjectId == projectId)
            .Select(item => item.Id).ToListAsync(ct).ConfigureAwait(false);
        var projectServerIds = await db.ProjectServers.AsNoTracking().Where(item => item.ProjectId == projectId)
            .Select(item => item.Id).ToListAsync(ct).ConfigureAwait(false);

        var libraries = await db.VariableLibraries.AsNoTracking()
            .Where(item => item.ProjectId == projectId
                || (item.EnvironmentId != null && environmentIds.Contains(item.EnvironmentId.Value))
                || (item.ProjectServerId != null && projectServerIds.Contains(item.ProjectServerId.Value)))
            .Select(item => new
            {
                item.Id,
                item.Name,
                item.EnvironmentId,
                item.ProjectServerId,
                Entries = item.Entries.Select(entry => new { entry.Key, entry.Value }).ToList()
            })
            .ToListAsync(ct).ConfigureAwait(false);
        var libraryIdsOfProject = libraries.Select(item => item.Id).ToList();

        var environments = await db.Environments.AsNoTracking()
            .Where(item => item.ProjectId == projectId)
            .Select(item => new
            {
                item.Id,
                item.Name,
                Servers = item.Servers.Select(link => new
                {
                    link.Server.Id,
                    link.Server.Name,
                    link.Server.OrganizationId,
                    link.Server.Status,
                    link.Server.AgentCapabilitiesJson,
                    Deleted = link.Server.DeletedAt != null
                }).ToList()
            })
            .ToListAsync(ct).ConfigureAwait(false);

        var projectServers = await db.ProjectServers.AsNoTracking()
            .Where(item => item.ProjectId == projectId)
            .Select(item => new
            {
                item.Id,
                item.DisplayName,
                Server = item.Server == null ? null : new
                {
                    item.Server.Id,
                    item.Server.Name,
                    item.Server.OrganizationId,
                    item.Server.Status,
                    item.Server.AgentCapabilitiesJson,
                    Deleted = item.Server.DeletedAt != null
                }
            })
            .ToListAsync(ct).ConfigureAwait(false);

        var policies = await db.BackupPolicies.AsNoTracking()
            .Where(item => item.ProjectId == projectId)
            .Select(item => new
            {
                item.Id,
                Server = new
                {
                    item.Server.Id,
                    item.Server.Name,
                    item.Server.OrganizationId,
                    item.Server.Status,
                    item.Server.AgentCapabilitiesJson,
                    Deleted = item.Server.DeletedAt != null
                }
            })
            .ToListAsync(ct).ConfigureAwait(false);

        var otherProjects = includeOtherProjects
            ? await LoadOtherProjectsAsync(projectId, project.OrganizationId, libraryIdsOfProject, ct).ConfigureAwait(false)
            : [];
        var deployServers = await LoadProductionDeployServersAsync(projectId, ct).ConfigureAwait(false);

        return new ProjectServerCleanupInputs(
            projectId,
            project.Name,
            project.OrganizationId,
            [.. libraries.Select(item => new CleanupLibrary(item.Name, item.EnvironmentId, item.ProjectServerId,
                item.Entries.GroupBy(entry => entry.Key, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal)))],
            [.. environments.Select(item => new CleanupEnvironment(item.Id, item.Name,
                [.. item.Servers.Select(server => new CleanupServerInfo(server.Id, server.Name, server.OrganizationId,
                    server.Status, server.AgentCapabilitiesJson, server.Deleted))]))],
            [.. projectServers.Select(item => new CleanupProjectServer(item.Id, item.DisplayName,
                item.Server is null ? null : new CleanupServerInfo(item.Server.Id, item.Server.Name, item.Server.OrganizationId,
                    item.Server.Status, item.Server.AgentCapabilitiesJson, item.Server.Deleted)))],
            [.. policies.Select(item => new CleanupBackupPolicy(item.Id, new CleanupServerInfo(item.Server.Id, item.Server.Name,
                item.Server.OrganizationId, item.Server.Status, item.Server.AgentCapabilitiesJson, item.Server.Deleted)))],
            otherProjects,
            deployServers);
    }

    /// <summary>The <c>deployServer</c> parameter is, by the contract of the generic deployment templates
    /// (<c>application-deploy-prod</c>, <c>application-release-fast</c>, both rendering <c>&lt;slug&gt;.prod</c>), the
    /// server production runs on; the setup wizard writes the chosen server as its default (recette R-21). A default that
    /// is an expression, or a definition that does not parse, names nothing.</summary>
    private async Task<List<string>> LoadProductionDeployServersAsync(int projectId, CancellationToken ct)
    {
        var definitions = await db.Pipelines.AsNoTracking()
            .Where(item => item.ProjectId == projectId)
            .OrderBy(item => item.Id)
            .Select(item => item.YamlDefinition)
            .ToListAsync(ct).ConfigureAwait(false);
        return [.. definitions
            .Select(yaml => YamlParsingHelper.ParseAndValidate(yaml))
            .SelectMany(definition => definition?.Parameters ?? [])
            .Where(parameter => string.Equals(parameter.Name, DeployServerParameter, StringComparison.Ordinal))
            .Select(parameter => parameter.Default?.Trim())
            .OfType<string>()
            .Where(name => name.Length > 0 && !name.Contains('$', StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)];
    }

    private const string DeployServerParameter = "deployServer";

    public Task AddTaskAsync(ServerTask task, CancellationToken ct = default) =>
        ServerTaskRepositoryOperations.AddTaskAsync(db, task, ct);

    public Task<List<ServerTask>> GetProjectTasksAsync(int projectId, IReadOnlyCollection<int> taskIds, CancellationToken ct = default)
    {
        var target = projectId.ToString(CultureInfo.InvariantCulture);
        return db.Tasks.AsNoTracking()
            .Where(task => taskIds.Contains(task.Id)
                && (task.Operation == OperationKind.ProjectResourcesDiscover || task.Operation == OperationKind.ProjectResourcesRemove)
                && task.Command == target)
            .OrderBy(task => task.Id)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyDictionary<int, DateTime>> GetLastProjectActivityAsync(
        int projectId, IReadOnlyCollection<int> serverIds, CancellationToken ct = default)
    {
        var last = await (
                from task in db.Tasks.AsNoTracking()
                join run in db.PipelineRuns.AsNoTracking() on task.PipelineRunId equals (int?)run.Id
                join pipeline in db.Pipelines.AsNoTracking() on run.PipelineId equals pipeline.Id
                where pipeline.ProjectId == projectId && serverIds.Contains(task.ServerId)
                group task by task.ServerId into tasks
                select new { ServerId = tasks.Key, Last = tasks.Max(item => item.CreatedAt) })
            .ToListAsync(ct).ConfigureAwait(false);
        return last.ToDictionary(item => item.ServerId, item => item.Last);
    }

    /// <summary>Every library outside the project, grouped by the project that owns it (directly, through an
    /// environment or a project server), plus the slugs of the organization's other projects.</summary>
    private async Task<List<CleanupForeignProject>> LoadOtherProjectsAsync(
        int projectId, int organizationId, List<int> libraryIdsOfProject, CancellationToken ct)
    {
        var projects = await db.Projects.AsNoTracking()
            .Where(item => item.Id != projectId)
            .OrderBy(item => item.Id)
            .Select(item => new { item.Id, item.Name, item.OrganizationId })
            .ToListAsync(ct).ConfigureAwait(false);
        var libraries = await db.VariableLibraries.AsNoTracking()
            .Where(item => !libraryIdsOfProject.Contains(item.Id))
            .Select(item => new
            {
                item.Name,
                Owner = item.ProjectId
                    ?? (item.Environment == null ? (int?)null : item.Environment.ProjectId)
                    ?? (item.ProjectServer == null ? (int?)null : item.ProjectServer.ProjectId),
                Entries = item.Entries.Select(entry => new { entry.Key, entry.Value }).ToList()
            })
            .ToListAsync(ct).ConfigureAwait(false);
        var byOwner = libraries.ToLookup(item => item.Owner);
        CleanupLibrary ToLibrary(string name, IEnumerable<(string Key, string Value)> entries) => new(name, null, null,
            entries.GroupBy(entry => entry.Key, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal));

        var result = projects.Select(item => new CleanupForeignProject(
                ProjectResourceNames.Slug(item.Name, item.Id), item.OrganizationId == organizationId,
                [.. byOwner[item.Id].Select(library => ToLibrary(library.Name, library.Entries.Select(entry => (entry.Key, entry.Value))))]))
            .ToList();
        // Libraries no project owns (organization-wide) still name things this project must not touch.
        result.Add(new CleanupForeignProject(string.Empty, false,
            [.. byOwner[null].Select(library => ToLibrary(library.Name, library.Entries.Select(entry => (entry.Key, entry.Value))))]));
        return result;
    }

    public async Task<IReadOnlyDictionary<int, string>> GetTaskOutputsAsync(IReadOnlyCollection<int> taskIds, CancellationToken ct = default)
    {
        if (taskIds.Count == 0) return new Dictionary<int, string>();
        var lines = await db.TaskLogs.AsNoTracking()
            .Where(log => taskIds.Contains(log.TaskId))
            .OrderBy(log => log.Id)
            .Select(log => new { log.TaskId, log.Message })
            .ToListAsync(ct).ConfigureAwait(false);
        var byTask = lines.GroupBy(line => line.TaskId)
            .ToDictionary(group => group.Key, group => string.Join('\n', group.Select(line => line.Message)));
        return taskIds.Distinct().ToDictionary(id => id, id => byTask.GetValueOrDefault(id, string.Empty));
    }
}
