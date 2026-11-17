// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Pipelines;

namespace Aetheus.Back.Components.Analysis.DependencyUpdates;

/// <summary>Whether the lock files of an update can be refreshed: the pipeline and the owner it runs as, or the reason it cannot.</summary>
internal sealed record DependencyUpdateLockReadiness(int? PipelineId, string? Owner, string? Refusal)
{
    public static DependencyUpdateLockReadiness Refused(string reason) => new(null, null, reason);
}

/// <summary>One launch of the lock refresh: on the update's branch, at the commit that holds the bumped manifest.</summary>
internal sealed record DependencyUpdateLockLaunch(int PipelineId, string Owner, string Branch, string Commit, string Command, int UpdateId);

/// <summary>The lock refresh side of an update (R-6): the project's <c>cve-lock-refresh</c> run, under its owner only.</summary>
internal interface IDependencyUpdateLockRefresh
{
    /// <summary>Checked before anything is written, so a branch whose lock files nobody may refresh is never left behind.</summary>
    Task<DependencyUpdateLockReadiness> CheckAsync(int projectId, string baseBranch, CancellationToken ct);

    /// <summary>Checked again when the run ends, before its lock files are committed: the run may have outlived the owner's rights.
    /// Null when the owner recorded at the launch is still the pipeline's owner and still may run it.</summary>
    Task<string?> RecheckAsync(int pipelineId, string owner, CancellationToken ct);

    Task<DependencyUpdateLaunch> LaunchAsync(DependencyUpdateLockLaunch launch, CancellationToken ct);

    Task<CveRemediationLocks> ReadLocksAsync(int runId, IReadOnlyList<string> paths, CancellationToken ct);
}

/// <summary>
/// R-6 (decision of 2026-11-09, option A): the lock files of an automatic update are refreshed by the remediation's own run
/// (<see cref="CveRemediationLockRun"/>: pipeline <c>cve-lock-refresh</c>, command in <c>LOCK_COMMAND</c>, files handed back as the
/// artifact <c>dependency-locks</c>) launched under the identity of that pipeline's OWNER, never a system or admin identity. The owner
/// must exist, be active, hold <c>pipeline.run</c> on the pipeline and Admin on every server the run targets (the automated launch
/// checks the servers, F-EXEC-1b); any of these missing, nothing is launched and the reason is returned, with no other identity
/// tried. The launch's audit entries name the owner (<see cref="IAuditActor"/>), so the run page says who it ran as.
/// </summary>
internal sealed class DependencyUpdateLockRefresh(
    DependencyUpdateRepository repository,
    IResourceAuthorizationService authz,
    IPipelineRunService runs,
    CveRemediationLockRun lockRun,
    IServiceScopeFactory scopes) : IDependencyUpdateLockRefresh
{
    public const string TriggerSource = "DependencyUpdateLockRefresh";
    private const string Pipeline = CveRemediationLockRun.PipelineName;

    public async Task<DependencyUpdateLockReadiness> CheckAsync(int projectId, string baseBranch, CancellationToken ct)
    {
        if (await repository.FindOwnedPipelineAsync(projectId, Pipeline, ct).ConfigureAwait(false) is not { } pipeline)
            return DependencyUpdateLockReadiness.Refused($"The project has no pipeline named '{Pipeline}' to run the package manager.");
        if (await OwnerRefusalAsync(pipeline.Id, pipeline.Owner, ct).ConfigureAwait(false) is { } refusal)
            return DependencyUpdateLockReadiness.Refused(refusal);

        PipelineRunPreparation? preparation;
        try
        {
            preparation = await runs.PrepareRunAsync(pipeline.Id, baseBranch, ct: ct).ConfigureAwait(false);
        }
        catch (BadRequestException problem)
        {
            // Mandatory catch: a definition that cannot be prepared is a refusal of this update, said on its row.
            return DependencyUpdateLockReadiness.Refused($"The pipeline '{Pipeline}' cannot be prepared: {problem.Message}");
        }

        if (preparation is null) return DependencyUpdateLockReadiness.Refused($"The pipeline '{Pipeline}' cannot be prepared.");
        return await AdministersAsync(authz, pipeline.Owner!, preparation.TargetServerIds, ct).ConfigureAwait(false)
            ? new DependencyUpdateLockReadiness(pipeline.Id, pipeline.Owner, null)
            : DependencyUpdateLockReadiness.Refused($"The owner '{pipeline.Owner}' of the pipeline '{Pipeline}' is not Admin of every server it targets.");
    }

    public async Task<string?> RecheckAsync(int pipelineId, string owner, CancellationToken ct)
    {
        if (await repository.FindOwnedPipelineAsync(pipelineId, ct).ConfigureAwait(false) is not { } pipeline)
            return $"The pipeline '{Pipeline}' no longer exists.";
        if (!string.Equals(pipeline.Owner, owner, StringComparison.Ordinal))
            return $"The owner of the pipeline '{Pipeline}' changed since the run was launched as '{owner}'.";
        return await OwnerRefusalAsync(pipelineId, owner, ct).ConfigureAwait(false);
    }

    /// <summary>The owner's own standing, read from the accounts, not from a cache of rights: an account deleted or disabled is said as such.</summary>
    private async Task<string?> OwnerRefusalAsync(int pipelineId, string? owner, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(owner))
            return $"The pipeline '{Pipeline}' has no owner, and the lock refresh runs only under its owner's rights: re-save it as an authorized user.";
        switch (await repository.IsUserActiveAsync(owner, ct).ConfigureAwait(false))
        {
            case null:
                return $"The owner '{owner}' of the pipeline '{Pipeline}' no longer exists, and the lock refresh runs under no other identity.";
            case false:
                return $"The owner '{owner}' of the pipeline '{Pipeline}' is disabled, and the lock refresh runs under no other identity.";
        }

        return await authz.HasPermissionAsync(owner, PermissionKeys.PipelineRun, pipelineId, ct).ConfigureAwait(false)
            ? null
            : $"The owner '{owner}' of the pipeline '{Pipeline}' may not run it (permission {PermissionKeys.PipelineRun}).";
    }

    /// <summary>In a scope of its own that acts for the owner: the launch's audit entries (and the run's lineage) name the owner, and
    /// nothing else of the pass is attributed to them.</summary>
    public async Task<DependencyUpdateLaunch> LaunchAsync(DependencyUpdateLockLaunch launch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(launch);
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IAuditActor>().ActAs(launch.Owner);
        var scopedRuns = scope.ServiceProvider.GetRequiredService<IPipelineRunService>();
        var scopedAuthz = scope.ServiceProvider.GetRequiredService<IResourceAuthorizationService>();
        var variables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PipelineRunService.SourceBranchVariable] = launch.Branch,
            [PipelineRunService.SourceCommitVariable] = launch.Commit,
            [DependencyUpdatePipelines.UpdateVariable] = launch.UpdateId.ToString(CultureInfo.InvariantCulture)
        };
        try
        {
            return await LaunchInScopeAsync(scopedRuns, scopedAuthz, launch, variables, ct).ConfigureAwait(false);
        }
        catch (BadRequestException refusal)
        {
            // Mandatory catch: an automated preparation or launch refuses by throwing, after recording a failed run that carries the
            // reason. The update says why its lock files were not refreshed.
            return new DependencyUpdateLaunch(null, refusal.Message);
        }
    }

    private static async Task<DependencyUpdateLaunch> LaunchInScopeAsync(
        IPipelineRunService scopedRuns, IResourceAuthorizationService scopedAuthz, DependencyUpdateLockLaunch launch,
        Dictionary<string, string> variables, CancellationToken ct)
    {
        // The automated path authorizes the pipeline's owner on the servers of the branch's definition, and records a refusal as a run.
        var preparation = await scopedRuns.PrepareAutomatedRunAsync(launch.PipelineId, TriggerSource, variables, ct).ConfigureAwait(false);
        if (preparation is null)
            return new DependencyUpdateLaunch(null, "The run was not authorized for the pipeline's owner; its runs list says why.");
        if (!string.Equals(preparation.Pipeline.CreatedByUsername, launch.Owner, StringComparison.Ordinal))
            return await RefuseAsync(scopedRuns, launch, variables, $"The owner of the pipeline '{Pipeline}' changed since it was checked.", ct)
                .ConfigureAwait(false);

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal) { [CveRemediationLockRun.CommandParameter] = launch.Command };
        var resolved = await scopedRuns.ResolveRunParametersAsync(preparation, parameters, ct).ConfigureAwait(false);
        // The command may move the run to other servers: the owner is authorized again on the exact targets.
        if (!await AdministersAsync(scopedAuthz, launch.Owner, resolved.TargetServerIds, ct).ConfigureAwait(false))
            return await RefuseAsync(scopedRuns, launch, variables,
                $"The owner '{launch.Owner}' of the pipeline '{Pipeline}' is not Admin of every server the run targets.", ct).ConfigureAwait(false);

        var run = await scopedRuns.TriggerPreparedRunAsync(resolved with { InitiatedBy = launch.Owner }, variables, parameters, ct,
            idempotencyKey: $"dependency-update-lock-{launch.UpdateId}").ConfigureAwait(false);
        return run is null ? new DependencyUpdateLaunch(null, $"The pipeline '{Pipeline}' returned no run.") : new DependencyUpdateLaunch(run.Id, null);
    }

    private static async Task<DependencyUpdateLaunch> RefuseAsync(
        IPipelineRunService scopedRuns, DependencyUpdateLockLaunch launch, Dictionary<string, string> variables, string reason, CancellationToken ct)
    {
        await scopedRuns.RecordRefusedAutomatedLaunchAsync(launch.PipelineId, TriggerSource, reason, variables, ct).ConfigureAwait(false);
        return new DependencyUpdateLaunch(null, reason);
    }

    private static async Task<bool> AdministersAsync(IResourceAuthorizationService rights, string owner, IReadOnlyCollection<int> servers, CancellationToken ct)
    {
        foreach (var server in servers)
        {
            if (!await rights.HasPermissionAsync(owner, ResourceType.Server, server, Permission.Admin, ct).ConfigureAwait(false)) return false;
        }

        return true;
    }

    public Task<CveRemediationLocks> ReadLocksAsync(int runId, IReadOnlyList<string> paths, CancellationToken ct) =>
        lockRun.ReadLocksAsync(runId, paths, ct);
}
