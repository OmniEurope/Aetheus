// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using System.Text.Json;
using Aetheus.Back.Components.Analysis.DependencyBump;
using Aetheus.Back.Components.Inventory;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Analysis;

/// <summary>How a request ended: a new attempt, the attempt already under way, or the command to run instead.</summary>
public enum CveRemediationAnswer
{
    Started,
    AlreadyActive,
    Fallback
}

public sealed record CveRemediationOutcome(CveRemediationAnswer Answer, CveRemediationDto Remediation);

public interface ICveRemediationService
{
    /// <summary>The match, when the caller may read it: nothing else of it is read, and no repository is touched, before this answers.
    /// <paramref name="organizationIds"/> is null for a site Admin; the caller then checks the right the action needs.</summary>
    Task<CveRemediationScope?> FindScopeAsync(
        long matchId, IReadOnlyCollection<int>? organizationIds, IReadOnlyCollection<int>? readableProjectIds, CancellationToken ct = default);

    /// <summary>Prepares the fix of a match, for a caller already checked on the project of <paramref name="scope"/>: the branch, the
    /// lock file through a run, the pull request. Asking twice finds the first attempt. What cannot be done answers the command to run.</summary>
    Task<CveRemediationOutcome> RequestAsync(CveRemediationScope scope, ClaimsPrincipal user, CancellationToken ct = default);

    /// <summary>The latest attempt, brought up to what the repository says.</summary>
    Task<CveRemediationDto> GetAsync(CveRemediationScope scope, CancellationToken ct = default);
}

internal sealed class CveRemediationService(
    CveRemediationRepository repository,
    ICveWatchQueryService query,
    CveRemediationGit git,
    CveRemediationLockRun lockRun,
    CveRemediationFollowUp followUp,
    CveRemediationLedger ledger,
    TimeProvider timeProvider) : ICveRemediationService
{
    private sealed record Attempt(
        CveRemediationFacts Facts, CveRemediationRepositoryRow Repository, string Branch, string Target,
        DependencyBumpResult Plan, string BaseCommit, int? RunPipelineId, string? LockCommand);

    private sealed record Preparation(Attempt? Attempt, CveRemediationFallbackDto? Fallback);

    public Task<CveRemediationScope?> FindScopeAsync(
        long matchId, IReadOnlyCollection<int>? organizationIds, IReadOnlyCollection<int>? readableProjectIds, CancellationToken ct = default) =>
        repository.FindScopeAsync(matchId, organizationIds, readableProjectIds, ct);

    public async Task<CveRemediationOutcome> RequestAsync(CveRemediationScope scope, ClaimsPrincipal user, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(user);
        if (await ExistingAsync(scope, ct).ConfigureAwait(false) is { } existing) return existing;
        var prepared = await PrepareAsync(scope, user, ct).ConfigureAwait(false);
        if (prepared.Attempt is not { } attempt)
        {
            await ledger.RecordRefusalAsync(scope.MatchId, prepared.Fallback!, ct).ConfigureAwait(false);
            return new CveRemediationOutcome(CveRemediationAnswer.Fallback, new CveRemediationDto { MatchId = scope.MatchId, Fallback = prepared.Fallback });
        }

        var remediation = NewRow(scope, attempt, user.Identity?.Name ?? string.Empty);
        if (!await repository.TryAddAsync(remediation, ct).ConfigureAwait(false))
            return await ExistingAsync(scope, ct).ConfigureAwait(false) ?? throw new ConflictException("Another request is preparing this fix.");

        // The row exists: from here the attempt no longer belongs to the request. A client that leaves neither interrupts it nor leaves it
        // active, which would block every later attempt, so the work runs to its end on no token of the caller.
        await ledger.RecordAsync(remediation, "RemediationRequested", $"{attempt.Facts.PackageName} {remediation.FromVersion} -> {remediation.TargetVersion}", CancellationToken.None).ConfigureAwait(false);
        var failure = await RunAsync(remediation, attempt, user, CancellationToken.None).ConfigureAwait(false);
        if (failure is null)
            return new CveRemediationOutcome(CveRemediationAnswer.Started, await ToDtoAsync(remediation, ct).ConfigureAwait(false));
        await ledger.FailAsync(remediation, failure).ConfigureAwait(false);
        return new CveRemediationOutcome(CveRemediationAnswer.Fallback, await ToDtoAsync(remediation, ct).ConfigureAwait(false));
    }

    public async Task<CveRemediationDto> GetAsync(CveRemediationScope scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var latest = await repository.GetLatestAsync(scope.MatchId, ct).ConfigureAwait(false);
        if (latest is null) return new CveRemediationDto { MatchId = scope.MatchId };
        await followUp.SyncAsync(latest, ct).ConfigureAwait(false);
        return await ToDtoAsync(latest, ct).ConfigureAwait(false);
    }

    private async Task<CveRemediationOutcome?> ExistingAsync(CveRemediationScope scope, CancellationToken ct)
    {
        var active = await repository.GetActiveAsync(scope.MatchId, ct).ConfigureAwait(false);
        if (active is null) return null;
        await followUp.SyncAsync(active, ct).ConfigureAwait(false);
        return new CveRemediationOutcome(CveRemediationAnswer.AlreadyActive, await ToDtoAsync(active, ct).ConfigureAwait(false));
    }

    /// <summary>Everything that can refuse the fix, before anything is written: a refusal leaves no branch and no row.</summary>
    private async Task<Preparation> PrepareAsync(CveRemediationScope scope, ClaimsPrincipal user, CancellationToken ct)
    {
        var facts = await repository.GetFactsAsync(scope.MatchId, ct).ConfigureAwait(false) ?? throw new NotFoundException("Match not found.");
        var input = new CveRemediationCommandInput(facts.Ecosystem, facts.PackageName, null, facts.SourcePath, facts.ItemKind);
        if (facts.Closed || facts.Status != VulnerabilityMatchStatus.Affected)
            return Refuse(input, CveRemediationFallbackReason.MatchNotOpen);

        var target = await TargetAsync(scope, ct).ConfigureAwait(false);
        input = input with { TargetVersion = target };
        if (target is null || !InventoryOrigins.IsFile(facts.SourcePath))
            return Refuse(input, CveRemediationFallbackReason.NoFileToChange);
        var row = facts.RepositoryId is { } repositoryId
            ? await repository.FindRepositoryAsync(repositoryId, scope.ProjectId, ct).ConfigureAwait(false)
            : null;
        if (row is null) return Refuse(input, CveRemediationFallbackReason.NoRepository);
        if (row.IsMirror)
            return Refuse(input, CveRemediationFallbackReason.ExternalMirror);

        var branch = CveRemediationBranch.For(facts.Advisory, facts.PackageName);
        if (await git.WriteRefusalAsync(row.Repository, branch, ct).ConfigureAwait(false) is { } refusal)
            return Refuse(input, CveRemediationFallbackReason.BranchProtected, refusal);
        if (await repository.IsBranchTakenAsync(row.Repository.Id, branch, scope.MatchId, ct).ConfigureAwait(false))
            return Refuse(input, CveRemediationFallbackReason.BranchInUse, $"Another match is preparing {branch}.");
        return await PlanAsync(scope, user, facts, row, branch, target, input, ct).ConfigureAwait(false);
    }

    private async Task<Preparation> PlanAsync(
        CveRemediationScope scope, ClaimsPrincipal user, CveRemediationFacts facts, CveRemediationRepositoryRow row, string branch, string target,
        CveRemediationCommandInput input, CancellationToken ct)
    {
        var request = new DependencyBumpRequest(facts.Ecosystem, facts.PackageName, facts.Version, target, facts.SourcePath!);
        var plan = await git.PlanAsync(row.Repository.Id, row.Repository.DefaultBranch, request, ct).ConfigureAwait(false);
        if (plan.Result is null) return Refuse(input, CveRemediationFallbackReason.NoRepository, plan.Failure!);
        if (plan.Result.Outcome == DependencyBumpOutcome.Refused)
            return Refuse(input, CveRemediationFallbackReason.BumpRefused, $"{plan.Result.Refusal}: {plan.Result.Reason}");
        var attempt = new Attempt(facts, row, branch, target, plan.Result, plan.BaseCommit!, null, null);
        if (plan.Result.Outcome == DependencyBumpOutcome.Complete) return new Preparation(attempt, null);

        if (CveRemediationLockRun.CommandLine(plan.Result.LockRefreshes) is not { } command)
            return Refuse(input, CveRemediationFallbackReason.BumpRefused, "The directory of a lock file is not a plain path.");
        var readiness = await lockRun.CheckAsync(user, scope.ProjectId, row.Repository.DefaultBranch, ct).ConfigureAwait(false);
        return readiness.PipelineId is { } pipelineId
            ? new Preparation(attempt with { RunPipelineId = pipelineId, LockCommand = command }, null)
            : Refuse(input, readiness.Refusal!.Value, readiness.Detail!);
    }

    /// <summary>A refusal. The detail is only what the localized reason does not already say (a branch, a rule, an error): empty otherwise.</summary>
    private static Preparation Refuse(CveRemediationCommandInput input, CveRemediationFallbackReason reason, string detail = "") =>
        new(null, CveRemediationFallback.For(input, reason, detail));

    /// <summary>The version the recommendation names, checked against the registry, when it is a change of a file.</summary>
    private async Task<string?> TargetAsync(CveRemediationScope scope, CancellationToken ct)
    {
        var detail = await query.GetDetailAsync(scope.MatchId, null, null, ct).ConfigureAwait(false);
        return detail?.Recommendation is { Kind: CveRecommendationKind.BumpDependency or CveRecommendationKind.MajorUpgrade or CveRecommendationKind.BumpBaseImage or CveRecommendationKind.BumpRuntime } recommendation
            ? recommendation.TargetVersion
            : null;
    }

    private VulnerabilityRemediation NewRow(CveRemediationScope scope, Attempt attempt, string requester)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        return new VulnerabilityRemediation
        {
            OrganizationId = scope.OrganizationId,
            ProjectId = scope.ProjectId,
            VulnerabilityMatchId = scope.MatchId,
            Status = VulnerabilityRemediationStatus.Requested,
            FromVersion = attempt.Facts.Version,
            TargetVersion = attempt.Target,
            Branch = attempt.Branch,
            BaseBranch = attempt.Repository.Repository.DefaultBranch,
            GitInternalRepoId = attempt.Repository.Repository.Id,
            LockFilesJson = attempt.Plan.LockRefreshes.Count == 0 ? null : JsonSerializer.Serialize(attempt.Plan.LockRefreshes.Select(refresh => refresh.LockFile)),
            RequestedByUsername = requester,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    /// <summary>Writes the branch, then the run or the pull request. Returns why it stopped, or null. Any failure is an honest answer with its
    /// reason: the attempt ends as failed and the branch stays for a retry, which reuses it.</summary>
    private async Task<string?> RunAsync(VulnerabilityRemediation remediation, Attempt attempt, ClaimsPrincipal user, CancellationToken ct)
    {
        var gitRepository = attempt.Repository.Repository;
        var title = CveRemediationText.Title(attempt.Facts.PackageName, attempt.Target, attempt.Facts.Advisory);
        try
        {
            var files = attempt.Plan.Edits.Select(edit => (edit.Path, edit.Content)).ToList();
            if (await git.PushAsync(gitRepository, attempt.Branch, attempt.BaseCommit, files, title, remediation.RequestedByUsername, ct).ConfigureAwait(false) is { } error)
                return error;
            await ledger.MoveAsync(remediation, VulnerabilityRemediationStatus.BranchPushed, $"Pushed {attempt.Branch}.", ct).ConfigureAwait(false);
            if (attempt.RunPipelineId is not { } pipelineId)
            {
                remediation.PullRequestId = await git.OpenPullRequestAsync(remediation, gitRepository, title, CveRemediationText.Description(attempt.Facts, remediation), ct).ConfigureAwait(false);
                await ledger.MoveAsync(remediation, VulnerabilityRemediationStatus.PullRequestOpen, $"Pull request {remediation.PullRequestId} opened.", ct).ConfigureAwait(false);
                return null;
            }

            var run = await lockRun.LaunchAsync(user, pipelineId, attempt.Branch, attempt.LockCommand!, remediation.Id, ct).ConfigureAwait(false);
            if (run is null) return $"The pipeline '{CveRemediationLockRun.PipelineName}' could not be launched on {attempt.Branch}.";
            remediation.PipelineRunId = run;
            await ledger.MoveAsync(remediation, VulnerabilityRemediationStatus.Running, $"Run {run} refreshes the lock files.", ct).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            return ledger.ReasonOf(remediation, ex);
        }
    }

    private async Task<CveRemediationDto> ToDtoAsync(VulnerabilityRemediation remediation, CancellationToken ct)
    {
        var pullRequest = remediation.PullRequestId is { } id ? await repository.GetPullRequestAsync(id, ct).ConfigureAwait(false) : null;
        return new CveRemediationDto
        {
            MatchId = remediation.VulnerabilityMatchId,
            Status = remediation.Status,
            FromVersion = remediation.FromVersion,
            TargetVersion = remediation.TargetVersion,
            ResolvedVersion = remediation.ResolvedVersion,
            Branch = remediation.Branch,
            BaseBranch = remediation.BaseBranch,
            PipelineRunId = remediation.PipelineRunId,
            PullRequestId = remediation.PullRequestId,
            PullRequestNumber = pullRequest?.Number,
            RequestedBy = remediation.RequestedByUsername,
            FailureReason = remediation.FailureReason,
            Fallback = remediation.Status == VulnerabilityRemediationStatus.Failed
                ? await FailedFallbackAsync(remediation, ct).ConfigureAwait(false)
                : null
        };
    }

    private async Task<CveRemediationFallbackDto?> FailedFallbackAsync(VulnerabilityRemediation remediation, CancellationToken ct)
    {
        var facts = await repository.GetFactsAsync(remediation.VulnerabilityMatchId, ct).ConfigureAwait(false);
        return facts is null
            ? null
            : CveRemediationFallback.For(
                new CveRemediationCommandInput(facts.Ecosystem, facts.PackageName, remediation.TargetVersion, facts.SourcePath, facts.ItemKind),
                CveRemediationFallbackReason.PreparationFailed, remediation.FailureReason ?? string.Empty);
    }
}
