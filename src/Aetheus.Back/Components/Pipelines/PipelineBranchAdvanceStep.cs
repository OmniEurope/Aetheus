// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Git;
using Aetheus.Back.Data.Entities;
using static Aetheus.Back.Components.Pipelines.PipelineRunHelpers;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Recette R2-001: <c>type: advance-branch</c>. A branch (<c>main</c> for production) is moved by an
/// explicit, visible step of the deploy pipeline onto the commit of the release that run deploys,
/// instead of by a per-environment switch that did nothing, without a trace, while it was off.
///
/// The commit is the one the release records, i.e. the one its candidate built, never the head of the
/// deploy run's own workspace (a newer <c>develop</c> that was never deployed). The release must already
/// be recorded as deployed, so the step belongs after the <c>type: release</c> step with
/// <c>deployed: true</c>. The ref is written by the backend, which hosts the repository
/// (<see cref="IGitBranchAdvanceService"/>, ADR-044): no credential reaches the agent.
///
/// Advanced, already-there and already-past (the branch contains the release) succeed and say so on
/// the step. A diverged branch succeeds too, with a run warning: the deployment before it worked, and
/// the branch is a follower of it, not a gate (recette R2-105). Every other outcome (no release, no
/// repository holding the commit, a refused ref update) fails the step with the reason, because a
/// branch that silently stays behind is the defect this replaces.
/// </summary>
public sealed class PipelineBranchAdvanceStep(
    IPipelineRepository pipelines,
    IGitBranchAdvanceService branchAdvance,
    IAuditService audit,
    TimeProvider timeProvider) : IPipelineBranchAdvanceStep
{
    /// <summary>The run variable naming the release a deploy pipeline delivers.</summary>
    internal const string CandidateVersionVariable = "AETHEUS_CANDIDATE_VERSION";

    public async Task ExecuteAsync(
        int runId, PipelineStepRun stepRun, PipelineStepDefinition stepDef,
        Dictionary<string, string> legVars, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stepRun);
        ArgumentNullException.ThrowIfNull(stepDef);
        ArgumentNullException.ThrowIfNull(legVars);

        var target = await ResolveTargetAsync(runId, stepDef, legVars, ct).ConfigureAwait(false);
        if (target.Error is not null)
        {
            await FailAsync(runId, stepRun, target.Error, ct).ConfigureAwait(false);
            return;
        }

        var release = target.Release!;
        var commit = release.CommitHash!;
        var result = await branchAdvance
            .TryFastForwardAsync(target.ProjectId, target.Branch, commit, ct)
            .ConfigureAwait(false);
        var advance = await AdvanceMessageAsync(runId, stepRun, target, release, commit, result, ct).ConfigureAwait(false);
        if (advance is null) return;

        var tagged = await TagAsync(runId, stepRun, stepDef, legVars, target, release, commit, ct).ConfigureAwait(false);
        if (tagged is null) return;
        Succeed(stepRun, tagged.Length == 0 ? advance : advance + " " + tagged);
    }

    /// <summary>What the branch advance did, or null once the step has failed.</summary>
    private async Task<string?> AdvanceMessageAsync(
        int runId, PipelineStepRun stepRun, AdvanceTarget target, BranchAdvanceRelease release, string commit,
        GitBranchAdvanceResult result, CancellationToken ct)
    {
        switch (result.Outcome)
        {
            case GitBranchAdvanceOutcome.Advanced:
                // Audited: the only path that moves a branch without anyone pushing to it, so "who
                // moved main and why" must be answerable from the audit log alone.
                await audit.LogAsync(
                    "AdvancedBranchAfterDeploy", "Release", release.Id,
                    $"{target.Branch} -> {commit} in {result.RepositorySlug} (run {runId}, was {result.PreviousSha ?? "absent"})",
                    ct).ConfigureAwait(false);
                return $"'{target.Branch}' advanced from {Short(result.PreviousSha) ?? "(new branch)"} to {Short(commit)} "
                    + $"(release {release.Version}, repository {result.RepositorySlug}).";
            case GitBranchAdvanceOutcome.AlreadyUpToDate:
                return $"'{target.Branch}' already points at {Short(commit)} (release {release.Version}); nothing to advance.";
            case GitBranchAdvanceOutcome.AlreadyContains:
                // Recette R2-105: 'main' had been pushed past the candidate; the deployment succeeded and
                // its release is on the branch already. It failed the whole run.
                return $"'{target.Branch}' is at {Short(result.PreviousSha)}, which already contains {Short(commit)} "
                    + $"(release {release.Version}); nothing to advance.";
            case GitBranchAdvanceOutcome.NotFastForward:
                {
                    // A real divergence is worth a look, but the deployment it follows did succeed: it is a
                    // warning on the run, never a failed run (the replaced prod-advance-main.sh was best effort).
                    var reason = $"'{target.Branch}' is at {Short(result.PreviousSha)}, which has diverged from {Short(commit)} "
                        + $"(release {release.Version}): a fast-forward is impossible, the branch was left where it is.";
                    await pipelines.AppendRunWarningsAsync(
                        runId, [$"Advance branch step '{stepRun.StepName}': {reason}"], ct).ConfigureAwait(false);
                    return reason;
                }
            case GitBranchAdvanceOutcome.CommitNotFound:
                await FailAsync(runId, stepRun,
                    $"no internal repository of the project holds commit {commit} of release {release.Version}.",
                    ct).ConfigureAwait(false);
                return null;
            default:
                await FailAsync(runId, stepRun,
                    $"the advance of '{target.Branch}' to {Short(commit)} was refused: {result.Detail ?? "no detail"}.",
                    ct).ConfigureAwait(false);
                return null;
        }
    }

    /// <summary>
    /// Decision of 2026-10-07: the optional <c>tag</c> names the deployed commit after the deployed version.
    /// Empty when the step declares no tag, null once the step has failed. The tag goes on the release's
    /// commit whatever the branch did, since that commit is what production now runs.
    /// </summary>
    private async Task<string?> TagAsync(
        int runId, PipelineStepRun stepRun, PipelineStepDefinition stepDef, Dictionary<string, string> legVars,
        AdvanceTarget target, BranchAdvanceRelease release, string commit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(stepDef.Tag)) return string.Empty;
        var tag = SubstituteVariables(stepDef.Tag, legVars).Trim();
        if (tag.Length == 0 || tag.Contains("$(", StringComparison.Ordinal))
        {
            await FailAsync(runId, stepRun,
                $"the tag '{stepDef.Tag}' names a variable this run does not set, so the deployed version has no tag.",
                ct).ConfigureAwait(false);
            return null;
        }

        var result = await branchAdvance.TryCreateTagAsync(target.ProjectId, tag, commit, ct).ConfigureAwait(false);
        switch (result.Outcome)
        {
            case GitTagOutcome.Created:
                await audit.LogAsync(
                    "TaggedReleaseAfterDeploy", "Release", release.Id,
                    $"{tag} -> {commit} in {result.RepositorySlug} (run {runId})", ct).ConfigureAwait(false);
                return $"Tag '{tag}' created on {Short(commit)}.";
            case GitTagOutcome.AlreadyThere:
                return $"Tag '{tag}' already on {Short(commit)}.";
            case GitTagOutcome.OnAnotherCommit:
                await FailAsync(runId, stepRun,
                    $"tag '{tag}' already names {Short(result.ExistingSha)}, not the deployed {Short(commit)}; it was left as it is.",
                    ct).ConfigureAwait(false);
                return null;
            case GitTagOutcome.CommitNotFound:
                await FailAsync(runId, stepRun,
                    $"no internal repository of the project holds commit {commit} to tag '{tag}'.", ct).ConfigureAwait(false);
                return null;
            default:
                await FailAsync(runId, stepRun,
                    $"the tag '{tag}' on {Short(commit)} was refused: {result.Detail ?? "no detail"}.", ct).ConfigureAwait(false);
                return null;
        }
    }

    private async Task<AdvanceTarget> ResolveTargetAsync(
        int runId, PipelineStepDefinition stepDef, Dictionary<string, string> legVars, CancellationToken ct)
    {
        var branch = SubstituteVariables(stepDef.Branch ?? string.Empty, legVars).Trim();
        if (branch.Length == 0)
            return AdvanceTarget.Failed("missing 'branch' to advance.");

        var version = SubstituteVariables($"$({CandidateVersionVariable})", legVars).Trim();
        if (version.Length == 0 || version.Contains("$(", StringComparison.Ordinal))
            return AdvanceTarget.Failed(
                $"the run sets no {CandidateVersionVariable}: the branch moves onto the commit of the release this run deploys, and the run names none.");

        var run = await pipelines.GetPipelineRunWithPipelineAsync(runId, ct).ConfigureAwait(false);
        var projectId = run?.Pipeline is null
            ? null
            : await pipelines.GetPipelineProjectIdAsync(run.Pipeline, ct).ConfigureAwait(false);
        if (projectId is not { } project)
            return AdvanceTarget.Failed("advance-branch is only supported for project-owned pipelines.");

        var release = await pipelines.FindBranchAdvanceReleaseAsync(project, version, ct).ConfigureAwait(false);
        if (release is null)
            return AdvanceTarget.Failed($"release '{version}' does not exist in this project.");
        if (release.Status != ReleaseStatus.Deployed)
            return AdvanceTarget.Failed(
                $"release '{version}' is {release.Status}, not Deployed: place this step after the type: release step with deployed: true.");
        if (string.IsNullOrWhiteSpace(release.CommitHash))
            return AdvanceTarget.Failed($"release '{version}' records no commit, so there is nothing to advance onto.");
        return new AdvanceTarget(project, branch, release, null);
    }

    private void Succeed(PipelineStepRun stepRun, string message)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        stepRun.Status = TaskExecutionStatus.Success;
        stepRun.ExitCode = 0;
        stepRun.StartedAt ??= now;
        stepRun.CompletedAt = now;
        // Recette R2-071: an advance that worked is the expected outcome of a delivery, not a warning.
        // It was appended to the run's warnings and shown in the yellow banner, which read as an error.
        // It stays on the step, as its output, and in the audit log.
        stepRun.OutputVariablesJson = System.Text.Json.JsonSerializer.Serialize(
            new Dictionary<string, string> { [OutcomeOutput] = message });
    }

    /// <summary>The step output that carries what the advance did.</summary>
    internal const string OutcomeOutput = "BRANCH_ADVANCE";

    private async Task FailAsync(int runId, PipelineStepRun stepRun, string reason, CancellationToken ct)
    {
        MarkSystemStepFailed(stepRun, TaskFailureCodes.ToolError, reason, timeProvider.GetUtcNow().UtcDateTime);
        await pipelines.AppendRunWarningsAsync(
            runId, [$"Advance branch step '{stepRun.StepName}': {reason}"], ct).ConfigureAwait(false);
    }

    private static string? Short(string? sha) => sha is { Length: > 12 } ? sha[..12] : sha;

    private sealed record AdvanceTarget(int ProjectId, string Branch, BranchAdvanceRelease? Release, string? Error)
    {
        public static AdvanceTarget Failed(string error) => new(0, string.Empty, null, error);
    }
}
