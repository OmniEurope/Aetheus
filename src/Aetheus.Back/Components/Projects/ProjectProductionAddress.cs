// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Monitoring;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Projects;

/// <summary>
/// R-29: where the project's production is served, read from the latest SUCCESSFUL production deployment run of the project: the
/// address that run deployed (<see cref="PipelineRunDeployedAddress"/>, the same reading as the run detail tile). A run counts as a
/// production deployment when a deploy step completed in a stage that declares no environment (the shipped production templates)
/// or an environment that is the project's production one (type Production, or named <c>prod</c> or <c>production</c>), and its
/// target is not the local self-deploy campaign. A project with no such run has no address: nothing is guessed.
/// </summary>
internal static class ProjectProductionAddress
{
    /// <summary>The names the project's production environment goes by when its type is not set, as the production setup reads them.</summary>
    private static readonly string[] ProductionEnvironmentNames = ["prod", "production"];

    /// <summary>Runs inspected: the newest successful runs that resolved a public address, a bound that keeps the project page cheap.</summary>
    internal const int RunsInspected = 10;

    // Only a run that resolved one of these variables can have deployed an address: the database filters on them, so the
    // snapshots of the project's other runs (every successful CI run) are never read.
    private const string BlueGreenOriginVariable = "BG_PUBLIC_ORIGIN";
    private const string PublicAppUrlVariable = "PUBLIC_APP_URL";

    public static async Task<PipelineRunDeployedAddressDto?> ReadAsync(AppDbContext db, int projectId, CancellationToken ct)
    {
        var runs = await db.PipelineRuns.AsNoTracking()
            .Where(run => run.Pipeline.ProjectId == projectId
                && run.Status == PipelineStatus.Success
                && run.ResolvedVariablesJson != null
                && (run.ResolvedVariablesJson.Contains(BlueGreenOriginVariable) || run.ResolvedVariablesJson.Contains(PublicAppUrlVariable)))
            .Include(run => run.StepRuns)
            .OrderByDescending(run => run.StartedAt)
            .Take(RunsInspected)
            .AsSplitQuery()
            .ToListAsync(ct).ConfigureAwait(false);
        if (runs.Count == 0) return null;

        var productionNames = await db.Environments.AsNoTracking()
            .Where(environment => environment.ProjectId == projectId && environment.Type == EnvironmentType.Production)
            .Select(environment => environment.Name)
            .ToListAsync(ct).ConfigureAwait(false);
        var accepted = new HashSet<string>([.. productionNames, .. ProductionEnvironmentNames], StringComparer.OrdinalIgnoreCase);
        return Read(runs, accepted);
    }

    /// <summary>The address of the first run, newest first, that deployed to production; null when none did.</summary>
    internal static PipelineRunDeployedAddressDto? Read(IEnumerable<PipelineRun> runsNewestFirst, IReadOnlySet<string> productionEnvironments)
    {
        foreach (var run in runsNewestFirst)
        {
            var variables = PipelineRunHelpers.DeserializeResolvedVariables(run.ResolvedVariablesJson);
            if (variables.TryGetValue(PipelineDeploymentTargetGuard.TargetVariable, out var target)
                && string.Equals(target.Trim(), PipelineDeploymentTargetGuard.Local, StringComparison.OrdinalIgnoreCase))
                continue;
            var address = PipelineRunDeployedAddress.Read(
                run, PipelineAdvisorySteps.ParseSnapshot(run.YamlSnapshot), variables,
                environment => string.IsNullOrWhiteSpace(environment) || productionEnvironments.Contains(environment.Trim()));
            if (address is not null) return address;
        }
        return null;
    }
}
