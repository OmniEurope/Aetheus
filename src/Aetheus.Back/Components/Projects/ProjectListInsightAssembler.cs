// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Projects;

/// <summary>The newest commit of one project, as the project list shows it.</summary>
internal sealed record ProjectLatestCommit(int ProjectId, int Id, string Sha, string? Message, DateTime Date);

/// <summary>The step a run is on: running first, then assigned, then the next pending non-system step.</summary>
internal sealed record ProjectListCurrentStep(string StageName, string StepName);

/// <summary>The root run a project's list row shows: the running one first, then the newest.</summary>
internal sealed record ProjectListRun(
    int ProjectId,
    int Id,
    string PipelineName,
    PipelineStatus Status,
    DateTime StartedAt,
    ProjectListCurrentStep? CurrentStep);

/// <summary>An enabled monitored application of a project, with its live analytics sessions.</summary>
internal sealed record ProjectListApp(
    int ProjectId,
    AppHealthStatus CurrentStatus,
    bool AnalyticsEnabled,
    EnvironmentType? EnvironmentType,
    int ActiveSessionCount);

/// <summary>Assembles one project list insight from the rows <see cref="ProjectRepository"/> reads; no
/// query of its own.</summary>
internal static class ProjectListInsightAssembler
{
    internal static ProjectProductionStatus ResolveProductionStatus(IEnumerable<AppHealthStatus> statuses)
    {
        var values = statuses.ToList();
        if (values.Count == 0 || values.Any(status => status == AppHealthStatus.Unknown))
            return ProjectProductionStatus.Unavailable;
        if (values.Any(status => status is AppHealthStatus.Down or AppHealthStatus.Degraded))
            return ProjectProductionStatus.Offline;
        return values.All(status => status == AppHealthStatus.Up)
            ? ProjectProductionStatus.Online
            : ProjectProductionStatus.Unavailable;
    }

    internal static string NormalizeStageName(string stageName) =>
        stageName.StartsWith("System:", StringComparison.Ordinal)
            ? stageName["System:".Length..]
            : stageName;
    /// <summary>The applications that stand for production: those explicitly in a Production
    /// environment, or, when a project has none, those attached to no environment.</summary>
    public static Dictionary<int, List<ProjectListApp>> ProductionAppsByProject(IEnumerable<ProjectListApp> apps) =>
        apps.GroupBy(app => app.ProjectId)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var explicitlyProduction = group
                        .Where(app => app.EnvironmentType == EnvironmentType.Production)
                        .ToList();
                    return explicitlyProduction.Count > 0
                        ? explicitlyProduction
                        : group.Where(app => app.EnvironmentType is null).ToList();
                });

    public static ProjectListInsight Build(
        int projectId,
        ProjectLatestCommit? commit,
        ProjectListRun? run,
        AnalysisGrade? grade,
        IReadOnlyCollection<ProjectListApp> productionApps)
    {
        var analyticsAvailable = productionApps.Any(app => app.AnalyticsEnabled);
        return new ProjectListInsight(
            projectId,
            commit?.Id,
            commit?.Sha,
            commit?.Message,
            commit?.Date,
            run?.Id,
            run?.PipelineName,
            run?.Status,
            run?.StartedAt,
            grade,
            ResolveProductionStatus(productionApps.Select(app => app.CurrentStatus)),
            analyticsAvailable
                ? productionApps.Where(app => app.AnalyticsEnabled).Sum(app => app.ActiveSessionCount)
                : null,
            run?.Status == PipelineStatus.Running,
            run is { Status: PipelineStatus.Running, CurrentStep: { } step }
                ? $"{NormalizeStageName(step.StageName)} · {step.StepName}"
                : null);
    }
}
