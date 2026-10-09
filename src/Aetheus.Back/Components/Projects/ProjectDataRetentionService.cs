// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Projects;

public sealed class ProjectDataRetentionService(
    IProjectDataRetentionRepository repository,
    IAuditService audit,
    TimeProvider timeProvider) : IProjectDataRetentionService
{
    public async Task<ProjectDataRetentionDto?> GetAsync(int projectId, CancellationToken ct = default)
    {
        var project = await repository.FindAsync(projectId, ct).ConfigureAwait(false);
        return project is null ? null : ToDto(project);
    }

    public async Task<ProjectDataRetentionDto?> UpdateAsync(
        int projectId, UpdateProjectDataRetentionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Profile))
            throw new BadRequestException("Unknown data retention profile.");
        var project = await repository.FindAsync(projectId, ct).ConfigureAwait(false);
        if (project is null) return null;

        var overrides = request.Overrides ?? DataRetentionOverrides.None;
        project.DataRetentionProfile = request.Profile;
        project.TelemetryExportIntervalMinutes = overrides.TelemetryExportIntervalMinutes;
        project.TelemetryDetailRetentionDays = overrides.TelemetryDetailRetentionDays;
        project.TelemetryAggregateRetentionMonths = overrides.TelemetryAggregateRetentionMonths;
        project.AnalysisDetailRetentionDays = overrides.AnalysisDetailRetentionDays;
        project.AppLogRetentionDays = overrides.AppLogRetentionDays;
        project.StorageBudgetMegabytes = request.StorageBudgetMegabytes;
        project.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        await repository.SaveChangesAsync(ct).ConfigureAwait(false);

        var dto = ToDto(project);
        await audit.LogAsync("DataRetentionUpdated", "Project", project.Id, Describe(dto), ct).ConfigureAwait(false);
        return dto;
    }

    internal static ProjectDataRetentionDto ToDto(Project project)
    {
        var overrides = OverridesOf(project);
        return new ProjectDataRetentionDto
        {
            ProjectId = project.Id,
            Profile = project.DataRetentionProfile,
            Overrides = overrides,
            Effective = DataRetentionProfiles.Resolve(project.DataRetentionProfile, overrides),
            StorageBudgetMegabytes = project.StorageBudgetMegabytes
        };
    }

    internal static DataRetentionOverrides OverridesOf(Project project) => new()
    {
        TelemetryExportIntervalMinutes = project.TelemetryExportIntervalMinutes,
        TelemetryDetailRetentionDays = project.TelemetryDetailRetentionDays,
        TelemetryAggregateRetentionMonths = project.TelemetryAggregateRetentionMonths,
        AnalysisDetailRetentionDays = project.AnalysisDetailRetentionDays,
        AppLogRetentionDays = project.AppLogRetentionDays
    };

    private static string Describe(ProjectDataRetentionDto dto) => string.Format(
        CultureInfo.InvariantCulture,
        "profile={0}; export={1} min; telemetry detail={2} d; aggregates={3} months; analysis detail={4} d; logs={5} d; budget={6} MB",
        dto.Profile, dto.Effective.TelemetryExportIntervalMinutes, dto.Effective.TelemetryDetailRetentionDays,
        dto.Effective.TelemetryAggregateRetentionMonths, dto.Effective.AnalysisDetailRetentionDays,
        dto.Effective.AppLogRetentionDays, dto.StorageBudgetMegabytes ?? ProjectStorageDefaults.DefaultBudgetMegabytes);
}
