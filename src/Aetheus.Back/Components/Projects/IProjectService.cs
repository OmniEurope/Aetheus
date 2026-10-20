// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Projects;

public interface IProjectService
{
    Task<PaginatedResult<ProjectDto>> GetProjectsAsync(ProjectPaginationRequest request, List<int>? accessibleIds = null, CancellationToken ct = default);
    Task<ProjectDetailDto?> GetProjectDetailAsync(int id, CancellationToken ct = default);
    /// <summary>Creates the project and subscribes <paramref name="creatorUserId"/> when the caller
    /// has a user row, so project events reach the creator.</summary>
    Task<ProjectDto> CreateProjectAsync(CreateProjectRequest request, int? creatorUserId, CancellationToken ct = default);
    Task<ProjectDto?> UpdateProjectAsync(int id, UpdateProjectRequest request, CancellationToken ct = default);
    Task<List<ProjectDto>> GetProjectsWithRepoUrlAsync(CancellationToken ct = default);
    Task<List<ProjectServerDto>> GetProjectServersAsync(int projectId, CancellationToken ct = default);
    Task<PaginatedResult<ProjectServerDto>> GetProjectServersPageAsync(
        int projectId, PaginationRequest request, CancellationToken ct = default);
    Task<ProjectServerDto> CreateProjectServerAsync(int projectId, CreateProjectServerRequest request, CancellationToken ct = default);
    Task<ProjectServerDto?> UpdateProjectServerAsync(int projectId, int projectServerId, UpdateProjectServerRequest request, CancellationToken ct = default);
    Task<bool> DeleteProjectServerAsync(int projectId, int projectServerId, CancellationToken ct = default);
    Task<PaginatedResult<ServerTaskDto>> GetProjectTasksAsync(int projectId, PaginationRequest request, CancellationToken ct = default);

    /// <summary>The server names across the project's tasks.</summary>
    Task<TaskFilterValuesDto> GetProjectTaskFilterValuesAsync(int projectId, CancellationToken ct = default);
    Task<PaginatedResult<TaskLogDto>> GetProjectLogsAsync(int projectId, PaginationRequest request, CancellationToken ct = default);
    Task<List<ProjectActivityDto>> GetProjectActivityAsync(int projectId, int count = 20, CancellationToken ct = default);
}
