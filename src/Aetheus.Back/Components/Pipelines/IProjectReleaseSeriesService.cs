// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Pipelines;

/// <summary>The version series of a project and the administrator's choice of the next version.</summary>
public interface IProjectReleaseSeriesService
{
    /// <summary>The series of the project with the number each one hands out next; null when the
    /// project does not exist.</summary>
    Task<List<ProjectReleaseSeriesDto>?> GetAsync(int projectId, CancellationToken ct = default);

    /// <summary>
    /// Makes the requested number the next release number of the series, audited. Null when the
    /// project does not exist.
    /// </summary>
    /// <exception cref="BadRequestException">The series is not <c>major.minor</c>.</exception>
    /// <exception cref="ConflictException">A run of the series already took that number.</exception>
    Task<ProjectReleaseSeriesDto?> SetNextAsync(
        int projectId, SetNextReleaseNumberRequest request, CancellationToken ct = default);
}
