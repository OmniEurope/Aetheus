// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Git.Events;
using Aetheus.Back.Components.Pipelines.Events;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services.DomainEvents;
using Aetheus.Shared.Components.Git;

namespace Aetheus.Back.Components.Analysis;

/// <summary>
/// What happens to a remediation after the request (PLAN-004, lot 4, point 12): the end of the run that refreshed the lock file, then the fate
/// of the pull request. A merge is read from the pull request and never assumed, and the version the base branch really holds is read at
/// that moment. The match is not closed here: the move of the default branch asks for a new inventory, which reads the same lock file
/// and closes the match, or keeps it open if the version it holds is still affected.
/// </summary>
internal sealed class CveRemediationFollowUp(
    CveRemediationRepository repository,
    CveRemediationGit git,
    CveRemediationLockRun lockRun,
    CveRemediationLedger ledger,
    IResourceAuthorizationService authz,
    TimeProvider timeProvider)
    : IDomainEventHandler<PipelineRunCompletedEvent>, IDomainEventHandler<GitBranchMovedEvent>
{
    /// <summary>Far above the Git writes of one step: an attempt silent for this long lost its writer to a restart.</summary>
    private static readonly TimeSpan MaxSilence = TimeSpan.FromMinutes(30);

    public async Task HandleAsync(PipelineRunCompletedEvent domainEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        foreach (var remediation in await repository.GetRunningOfRunAsync(domainEvent.PipelineRunId, ct).ConfigureAwait(false))
            await CompleteRunAsync(remediation, domainEvent).ConfigureAwait(false);
    }

    public async Task HandleAsync(GitBranchMovedEvent domainEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        foreach (var remediation in await repository.GetOpenOfRepositoryAsync(domainEvent.GitRepoId, ct).ConfigureAwait(false))
            await SyncAsync(remediation, ct).ConfigureAwait(false);
    }

    /// <summary>Brings a remediation up to what the repository and its run say. A closed pull request raises no event, and the event of a run
    /// can come before the run was saved on the row or be lost in a restart, so a read comes through here too.</summary>
    public async Task SyncAsync(VulnerabilityRemediation remediation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(remediation);
        if (remediation.Status == VulnerabilityRemediationStatus.Running)
        {
            await SyncRunAsync(remediation, ct).ConfigureAwait(false);
            return;
        }

        if (remediation.Status is VulnerabilityRemediationStatus.Requested or VulnerabilityRemediationStatus.BranchPushed
            && timeProvider.GetUtcNow().UtcDateTime - remediation.UpdatedAt > MaxSilence)
        {
            await ledger.FailAsync(remediation, "The attempt stopped without an answer: the back restarted while it was writing.").ConfigureAwait(false);
            return;
        }

        if (remediation is not { Status: VulnerabilityRemediationStatus.PullRequestOpen, PullRequestId: { } pullRequestId }) return;
        var pullRequest = await repository.GetPullRequestAsync(pullRequestId, ct).ConfigureAwait(false);
        if (pullRequest is null || pullRequest.Status == PullRequestStatus.Closed)
        {
            await ledger.MoveAsync(remediation, VulnerabilityRemediationStatus.Abandoned, "The pull request was closed without a merge.", ct).ConfigureAwait(false);
            return;
        }

        if (pullRequest.Status != PullRequestStatus.Merged) return;
        var detail = await ReadMergedVersionAsync(remediation, ct).ConfigureAwait(false);
        await ledger.MoveAsync(remediation, VulnerabilityRemediationStatus.Merged, detail, ct).ConfigureAwait(false);
    }

    private async Task<string> ReadMergedVersionAsync(VulnerabilityRemediation remediation, CancellationToken ct)
    {
        var paths = LockPaths(remediation);
        if (paths.Count == 0 || remediation.GitInternalRepoId is not { } repositoryId) return "Merged, no lock file to read.";
        var facts = await repository.GetFactsAsync(remediation.VulnerabilityMatchId, ct).ConfigureAwait(false);
        if (facts is null) return "Merged, the match is gone.";
        var files = new List<(string Path, string Content)>(paths.Count);
        foreach (var path in paths)
        {
            var bytes = await git.ReadAsync(repositoryId, remediation.BaseBranch!, path, ct).ConfigureAwait(false);
            if (bytes is null) return $"Merged, but '{path}' cannot be read on {remediation.BaseBranch}.";
            files.Add((path, System.Text.Encoding.UTF8.GetString(bytes)));
        }

        var resolution = CveRemediationResolved.Read(facts.Ecosystem, facts.PackageName, remediation.TargetVersion, files);
        remediation.ResolvedVersion = resolution.Version ?? remediation.ResolvedVersion;
        return resolution.Failure is null ? $"Merged, {facts.PackageName} {resolution.Version} on {remediation.BaseBranch}." : $"Merged: {resolution.Failure}";
    }

    private async Task SyncRunAsync(VulnerabilityRemediation remediation, CancellationToken ct)
    {
        var status = remediation.PipelineRunId is { } runId ? await repository.GetRunStatusAsync(runId, ct).ConfigureAwait(false) : null;
        if (status is null)
            await ledger.FailAsync(remediation, "The run of the lock refresh no longer exists.").ConfigureAwait(false);
        else if (status.Value.IsTerminal())
            await CompleteRunAsync(remediation, new PipelineRunCompletedEvent(remediation.PipelineRunId!.Value, status.Value)).ConfigureAwait(false);
    }

    /// <summary>The one way out of a run, for the event and for a read alike: whoever takes the end does the writes, the other stops here. From
    /// there it runs on no caller's token, and every failure ends the attempt: the dispatcher of the event only logs what a handler throws.</summary>
    private async Task CompleteRunAsync(VulnerabilityRemediation remediation, PipelineRunCompletedEvent run)
    {
        if (!await ledger.TryClaimRunEndAsync(remediation).ConfigureAwait(false)) return;
        var failure = await TryCommitLocksAsync(remediation, run).ConfigureAwait(false);
        if (failure is not null) await ledger.FailAsync(remediation, failure).ConfigureAwait(false);
    }

    private async Task<string?> TryCommitLocksAsync(VulnerabilityRemediation remediation, PipelineRunCompletedEvent run)
    {
        try
        {
            await ledger.RecordAsync(remediation, "RemediationRunEnded", $"Run {run.PipelineRunId} ended as {run.Status}.", CancellationToken.None).ConfigureAwait(false);
            return await CommitLocksAsync(remediation, run, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ledger.ReasonOf(remediation, ex);
        }
    }

    /// <summary>The requester's rights are read again: the run may have outlived them. Returns why the attempt fails, or null once the pull request is open.</summary>
    private async Task<string?> CommitLocksAsync(VulnerabilityRemediation remediation, PipelineRunCompletedEvent run, CancellationToken ct)
    {
        if (run.Status != PipelineStatus.Success) return $"The run of the lock refresh ended as {run.Status}.";
        if (!await authz.HasPermissionAsync(remediation.RequestedByUsername, ResourceType.Project, remediation.ProjectId, Permission.Write, ct).ConfigureAwait(false))
            return "The requester no longer has write access to the project.";
        var facts = await repository.GetFactsAsync(remediation.VulnerabilityMatchId, ct).ConfigureAwait(false);
        var row = remediation.GitInternalRepoId is { } repositoryId
            ? await repository.FindRepositoryAsync(repositoryId, remediation.ProjectId, ct).ConfigureAwait(false)
            : null;
        if (facts is null || row is null) return "The match or its repository no longer exists.";

        var locks = await lockRun.ReadLocksAsync(run.PipelineRunId, LockPaths(remediation), ct).ConfigureAwait(false);
        if (locks.Failure is not null) return locks.Failure;
        var resolution = CveRemediationResolved.Read(facts.Ecosystem, facts.PackageName, remediation.TargetVersion, locks.Files);
        remediation.ResolvedVersion = resolution.Version;
        if (resolution.Failure is not null) return resolution.Failure;

        var message = CveRemediationText.Title(facts.PackageName, remediation.TargetVersion, facts.Advisory);
        if (await git.PushAsync(row.Repository, remediation.Branch!, remediation.Branch!, locks.Files, message, remediation.RequestedByUsername, ct).ConfigureAwait(false) is { } error)
            return error;
        await ledger.MoveAsync(remediation, VulnerabilityRemediationStatus.BranchPushed, $"Lock files of run {run.PipelineRunId} committed on {remediation.Branch}.", ct).ConfigureAwait(false);
        remediation.PullRequestId = await git.OpenPullRequestAsync(remediation, row.Repository, message, CveRemediationText.Description(facts, remediation), ct).ConfigureAwait(false);
        await ledger.MoveAsync(remediation, VulnerabilityRemediationStatus.PullRequestOpen, $"Pull request {remediation.PullRequestId} opened.", ct).ConfigureAwait(false);
        return null;
    }

    private static List<string> LockPaths(VulnerabilityRemediation remediation) =>
        remediation.LockFilesJson is { } json ? JsonSerializer.Deserialize<List<string>>(json) ?? [] : [];
}
