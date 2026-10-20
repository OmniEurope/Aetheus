// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Pipelines;

/// <summary>What setting the next release number of a series came to.</summary>
public enum SetNextReleaseNumberOutcome
{
    ProjectNotFound,
    NumberAlreadyUsed,
    Set
}

/// <summary>The administration side of the release counter: read the series of a project, and set
/// the number the next release of one takes.</summary>
public interface IProjectReleaseSeriesRepository
{
    /// <summary>The project's series with the number each one hands out next, newest series first;
    /// null when the project is gone.</summary>
    Task<List<ProjectReleaseSeriesDto>?> GetSeriesAsync(int projectId, CancellationToken ct = default);

    /// <summary>
    /// Makes <paramref name="number"/> the next release number of the series, creating the series
    /// when it has none. Refused with <see cref="SetNextReleaseNumberOutcome.NumberAlreadyUsed"/>
    /// when a run of the series already took that number: a version is never given twice.
    /// </summary>
    Task<SetNextReleaseNumberOutcome> SetNextAsync(
        int projectId, string series, int number, CancellationToken ct = default);
}
