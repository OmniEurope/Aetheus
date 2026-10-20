// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Back.Components.Pipelines;

public sealed class ProjectReleaseSeriesService(
    IProjectReleaseSeriesRepository repository,
    IAuditService audit) : IProjectReleaseSeriesService
{
    public Task<List<ProjectReleaseSeriesDto>?> GetAsync(int projectId, CancellationToken ct = default) =>
        repository.GetSeriesAsync(projectId, ct);

    public async Task<ProjectReleaseSeriesDto?> SetNextAsync(
        int projectId, SetNextReleaseNumberRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var series = request.Series?.Trim() ?? string.Empty;
        if (!ReleaseSeries.IsValid(series))
            throw new BadRequestException("The series must be a major.minor version such as 1.4.");
        if (request.NextNumber is < 0 or > ReleaseSeries.MaxNumber)
            throw new BadRequestException($"The release number must be between 0 and {ReleaseSeries.MaxNumber}.");

        var version = $"{series}.{request.NextNumber.ToString(CultureInfo.InvariantCulture)}";
        var outcome = await repository.SetNextAsync(projectId, series, request.NextNumber, ct).ConfigureAwait(false);
        switch (outcome)
        {
            case SetNextReleaseNumberOutcome.ProjectNotFound:
                return null;
            case SetNextReleaseNumberOutcome.NumberAlreadyUsed:
                throw new ConflictException(
                    $"Version {version} was already taken by a run of this project; choose a number that no run took.");
        }

        await audit.LogAsync("NextReleaseNumberSet", "Project", projectId, $"next version {version}", ct).ConfigureAwait(false);
        return new ProjectReleaseSeriesDto { Series = series, NextNumber = request.NextNumber };
    }
}
