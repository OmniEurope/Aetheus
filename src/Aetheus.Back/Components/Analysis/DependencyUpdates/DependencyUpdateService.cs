// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.Analysis.DependencyBump;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Data.Entities;
using Aetheus.Shared.Components.DependencyUpdates;

namespace Aetheus.Back.Components.Analysis.DependencyUpdates;

/// <summary>
/// R-2 (option B, 2026-11-10): a match whose only fix crosses a major version gets the same treatment on its own separate branch,
/// flagged <see cref="DependencyUpdate.IsMajorUpgrade"/>: CI then QA, shown as "major, review required", never merged, promoted or deployed.
/// R-24 (2026-11-11): the same flow for a base image (<c>docker</c>) or a runtime version a Dockerfile or manifest of the project pins, for a
/// patch release of the same series only (<see cref="DependencyUpdateImageSelection"/>); a digest-pinned image moves to the digest of the target
/// tag read from its registry (<see cref="IImageDigestResolver"/>), and no license is judged (an image is not a shipped library).
/// PLAN-007 (recette R-9): after each matching pass of the watch, every open match a patch or minor version fixes, on a NuGet or npm
/// package (or, recette R-28, a Packagist one) of a project whose setting is on, gets its update: a branch <c>aetheus/deps/&lt;package&gt;-&lt;version&gt;</c> with the
/// bump committed through the platform's git service, then the project's CI launched on that branch at that commit (its QA follows
/// when the CI succeeds, see <see cref="DependencyUpdateQaFollowUp"/>). A bump whose lock file needs its package manager is first
/// completed by the project's <c>cve-lock-refresh</c> run under the pipeline's owner (R-6, <see cref="DependencyUpdateLockRefresh"/>),
/// and the CI starts once its lock files are committed on the branch. Never a candidate: no quality, no security, no release.
/// What cannot be done is recorded with its reason and nothing is written. A branch once written is never written again; a refusal
/// or a failure is evaluated again at the next pass. Nothing here deploys: a run on such a branch chains into no deployment.
/// </summary>
internal sealed class DependencyUpdateService(
    DependencyUpdateRepository repository,
    ICveWatchQueryService query,
    IDependencyUpdateGit git,
    DependencyUpdateCiLaunch ciLaunch,
    IDependencyUpdateLockRefresh lockRefresh,
    IImageDigestResolver digests,
    IEnumerable<IRegistryLicenseReader> licenseReaders,
    TimeProvider timeProvider,
    ILogger<DependencyUpdateService> logger)
{
    private const int Batch = 200;
    private const int MaxReasonLength = 2048;

    /// <summary>Evaluates every candidate of the pass. One that fails is logged and left for the next pass; the others go on.</summary>
    public async Task<int> RunPassAsync(CancellationToken ct)
    {
        var written = 0;
        long after = 0;
        while (await repository.GetCandidatesAsync(DependencyUpdateSelection.Ecosystems, after, Batch, ct).ConfigureAwait(false) is { Count: > 0 } candidates)
        {
            foreach (var candidate in candidates)
            {
                try
                {
                    if (await EvaluateAsync(candidate, ct).ConfigureAwait(false)) written++;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // Mandatory catch: one package of one project must not stop the updates of the others; it is found again next pass.
                    repository.DiscardUnsavedChanges();
                    logger.LogError(exception, "Dependency update of match {MatchId} failed; it stays pending", candidate.MatchId);
                }
            }

            after = candidates[^1].MatchId;
        }

        if (written > 0) logger.LogInformation("Dependency updates: {Count} branches written", written);
        return written;
    }

    /// <summary>One candidate. True when a branch was written.</summary>
    public async Task<bool> EvaluateAsync(DependencyUpdateCandidate candidate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        // A patch or minor branch from this version stops everything. A major branch (R-2) stops only another major one, checked below,
        // so a patch or minor fix released later still gets its own branch.
        if (await repository.HasBranchFromAsync(candidate.RepositoryId, candidate.Ecosystem, candidate.PackageName, candidate.Version, false, ct)
                .ConfigureAwait(false))
            return false;

        var detail = await query.GetDetailAsync(candidate.MatchId, null, null, ct).ConfigureAwait(false);
        var choice = DependencyUpdateSelection.Choose(candidate.Ecosystem, candidate.Version, detail?.Recommendation);
        if (choice.IsMajor
            && await repository.HasBranchFromAsync(candidate.RepositoryId, candidate.Ecosystem, candidate.PackageName, candidate.Version, true, ct)
                .ConfigureAwait(false))
            return false;
        var key = choice.Version ?? detail?.Recommendation?.TargetVersion ?? candidate.FixedVersion;
        var update = await repository.FindAsync(candidate.RepositoryId, candidate.Ecosystem, candidate.PackageName, key, ct).ConfigureAwait(false);
        // A branch once written is never written again, whatever came after it (a lock refresh waiting, failed, or done).
        if (update is { Branch: not null }) return false;
        update ??= NewRow(candidate, key);
        update.IsMajorUpgrade = choice.IsMajor;

        if (choice.Refusal is { } refusal) return await RefuseAsync(update, refusal, ct).ConfigureAwait(false);
        var target = choice.Version!;
        // The license judges a library the product ships and resells. A base image or a runtime version is not one: no SBOM names it as a
        // component, so the judgment does not apply and is not faked; the row carries no license.
        if (!DependencyUpdateImageSelection.Handles(candidate.Ecosystem))
        {
            var license = await JudgeLicenseAsync(candidate, target, ct).ConfigureAwait(false);
            if (!license.Allowed) return await RefuseAsync(update, license.Refusal!, ct).ConfigureAwait(false);
            update.License = license.License;
            update.LicenseOfTargetVersion = license.LicenseOfTargetVersion;
        }

        return await WriteToRepositoryAsync(update, candidate, target, ct).ConfigureAwait(false);
    }

    /// <summary>R-31 (NuGet), R-8 (npm, Packagist): the license of the target version is read from the registry that publishes it, for
    /// that exact version, and a registry that does not say refuses the update with the reason (evaluated again at the next pass). An SBOM
    /// is never read for it: it describes what a project already ships, not the version the update moves to.</summary>
    private async Task<DependencyLicenseVerdict> JudgeLicenseAsync(DependencyUpdateCandidate candidate, string target, CancellationToken ct)
    {
        var reader = licenseReaders.FirstOrDefault(one => string.Equals(one.Ecosystem, candidate.Ecosystem, StringComparison.Ordinal));
        if (reader is null)
            return DependencyUpdateLicense.Judge(null,
                $"No registry is read for the license of a {candidate.Ecosystem} package: the update is not written.");
        var answer = await reader.ReadAsync(candidate.PackageName, target, ct).ConfigureAwait(false);
        return DependencyUpdateLicense.Judge(
            answer.Expression is null ? null : new DependencyLicenseEvidence([answer.Expression], true),
            answer.Failure is null ? null : $"{answer.Failure} The update is not written.");
    }

    /// <summary>The licensed bump of <paramref name="update"/> written to the project's own repository, or refused when that
    /// repository is missing, a mirror, or cannot take the bump. True when a branch was written.</summary>
    private async Task<bool> WriteToRepositoryAsync(
        DependencyUpdate update, DependencyUpdateCandidate candidate, string target, CancellationToken ct)
    {
        var row = await git.FindRepositoryAsync(candidate.RepositoryId, candidate.ProjectId, ct).ConfigureAwait(false);
        if (row is null) return await RefuseAsync(update, "No readable internal repository of the project holds this package.", ct).ConfigureAwait(false);
        if (row.IsMirror)
            return await RefuseAsync(update,
                "The repository is a mirror of an external one: a branch here would be erased by the next synchronization, and the external repository is never written.",
                ct).ConfigureAwait(false);
        if (DependencyUpdateBranch.For(candidate.PackageName, target) is not { } branch)
            return await RefuseAsync(update, "The package name or the version gives no valid branch name.", ct).ConfigureAwait(false);

        // R-24: an image the repository pins by digest moves to the digest of the target tag, read from its registry now. Never a tag alone
        // where a digest was pinned, and no guess: a registry that does not say refuses the update with the reason.
        var (digest, unresolved) = await ResolveDigestAsync(candidate, target, ct).ConfigureAwait(false);
        if (unresolved is not null) return await RefuseAsync(update, unresolved, ct).ConfigureAwait(false);

        var plan = await git.PlanAsync(row.Repository.Id, row.Repository.DefaultBranch,
            new DependencyBumpRequest(candidate.Ecosystem, candidate.PackageName, candidate.Version, target, candidate.SourcePath, digest), ct)
            .ConfigureAwait(false);
        if (plan.Result is null) return await RefuseAsync(update, plan.Failure ?? "The repository cannot be read.", ct).ConfigureAwait(false);
        switch (plan.Result.Outcome)
        {
            case DependencyBumpOutcome.Refused:
                return await RefuseAsync(update, $"{plan.Result.Refusal}: {plan.Result.Reason}", ct).ConfigureAwait(false);
            case DependencyBumpOutcome.NeedsPackageManager:
                return await WriteForLockRefreshAsync(update, row.Repository, branch, plan.BaseCommit!, plan.Result, candidate, ct).ConfigureAwait(false);
        }

        if (!await WriteBranchAsync(update, row.Repository, branch, plan.BaseCommit!, plan.Result, candidate, ct).ConfigureAwait(false)) return false;
        Stamp(update, DependencyUpdateStatus.BranchPushed, null);
        await SaveAsync(update, ct).ConfigureAwait(false);
        await ciLaunch.LaunchAsync(update, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>The digest of the target tag when the candidate is an image pinned by digest (null digest and null reason otherwise), or the
    /// reason it could not be resolved.</summary>
    private async Task<(string? Digest, string? Refusal)> ResolveDigestAsync(DependencyUpdateCandidate candidate, string target, CancellationToken ct)
    {
        if (candidate.Ecosystem != DependencyUpdateImageSelection.Image || !candidate.Version.Contains('@', StringComparison.Ordinal)) return (null, null);
        var resolved = await digests.ResolveAsync(candidate.PackageName, target, ct).ConfigureAwait(false);
        return resolved.Digest is null
            ? (null, $"{candidate.SourcePath} pins {candidate.PackageName} by digest, and the digest of {target} could not be resolved: {resolved.Failure} No tag-only line is written.")
            : (resolved.Digest, null);
    }

    /// <summary>R-6: a bump whose lock file only its package manager can write. The project's <c>cve-lock-refresh</c> run refreshes it,
    /// launched under the pipeline's owner and nobody else: no pipeline, or an owner gone, disabled or without the rights, and nothing is
    /// written. Otherwise the branch gets the manifest, the run starts on it, and <see cref="DependencyUpdateLockFollowUp"/> commits the
    /// lock files it hands back on the same branch before the CI runs.</summary>
    private async Task<bool> WriteForLockRefreshAsync(
        DependencyUpdate update, GitInternalRepo repository, string branch, string baseCommit, DependencyBumpResult plan,
        DependencyUpdateCandidate candidate, CancellationToken ct)
    {
        var lockFiles = string.Join(", ", plan.LockRefreshes.Select(refresh => refresh.LockFile));
        if (CveRemediationLockRun.CommandLine(plan.LockRefreshes) is not { } command)
            return await RefuseAsync(update, $"A lock file ({lockFiles}) needs its package manager, and its directory is not a plain path.", ct)
                .ConfigureAwait(false);
        var readiness = await lockRefresh.CheckAsync(candidate.ProjectId, repository.DefaultBranch, ct).ConfigureAwait(false);
        if (readiness.Refusal is { } refusal)
            return await RefuseAsync(update, $"A lock file ({lockFiles}) needs its package manager, and it cannot be refreshed: {refusal}", ct)
                .ConfigureAwait(false);

        update.LockPipelineId = readiness.PipelineId;
        update.LockRefreshOwner = readiness.Owner;
        update.LockFilesJson = JsonSerializer.Serialize(plan.LockRefreshes.Select(refresh => refresh.LockFile));
        if (!await WriteBranchAsync(update, repository, branch, baseCommit, plan, candidate, ct).ConfigureAwait(false)) return false;
        Stamp(update, DependencyUpdateStatus.LockRefreshing, null);
        await SaveAsync(update, ct).ConfigureAwait(false);

        var launch = await lockRefresh.LaunchAsync(
            new DependencyUpdateLockLaunch(readiness.PipelineId!.Value, readiness.Owner!, branch, update.CommitSha!, command, update.Id), ct).ConfigureAwait(false);
        update.LockPipelineRunId = launch.RunId;
        if (launch.Refusal is { } notStarted)
            Stamp(update, DependencyUpdateStatus.Failed,
                $"The branch {branch} holds the manifest, but the lock refresh '{CveRemediationLockRun.PipelineName}' did not start as '{readiness.Owner}': {notStarted}");
        await SaveAsync(update, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Writes the bump on its branch. False, with the row failed and saved, when the branch could not be written.</summary>
    private async Task<bool> WriteBranchAsync(
        DependencyUpdate update, GitInternalRepo repository, string branch, string baseCommit, DependencyBumpResult plan,
        DependencyUpdateCandidate candidate, CancellationToken ct)
    {
        var message = $"chore(deps): bump {candidate.PackageName} from {candidate.Version} to {update.TargetVersion}\n\n"
                      + $"Fixes {candidate.Advisory}. Written by Aetheus (automatic dependency update); production is never deployed by it."
                      + (update.IsMajorUpgrade ? " Major upgrade: it needs a review before anything is merged." : string.Empty);
        var written = await git.WriteAsync(repository, branch, baseCommit, [.. plan.Edits.Select(edit => (edit.Path, edit.Content))], message, ct)
            .ConfigureAwait(false);
        if (written.Error is { } error)
        {
            Stamp(update, DependencyUpdateStatus.Failed, $"The branch {branch} could not be written: {error}");
            await SaveAsync(update, ct).ConfigureAwait(false);
            return false;
        }

        update.Branch = branch;
        update.CommitSha = written.CommitSha;
        return true;
    }

    private async Task<bool> RefuseAsync(DependencyUpdate update, string reason, CancellationToken ct)
    {
        if (update.Id != 0 && update.Status == DependencyUpdateStatus.Refused && update.Reason == Truncate(reason)) return false;
        Stamp(update, DependencyUpdateStatus.Refused, reason);
        await SaveAsync(update, ct).ConfigureAwait(false);
        return false;
    }

    private void Stamp(DependencyUpdate update, DependencyUpdateStatus status, string? reason)
    {
        update.Status = status;
        update.Reason = reason is null ? null : Truncate(reason);
        update.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
    }

    private async Task SaveAsync(DependencyUpdate update, CancellationToken ct)
    {
        if (update.Id == 0) repository.Add(update);
        await repository.SaveAsync(ct).ConfigureAwait(false);
    }

    private DependencyUpdate NewRow(DependencyUpdateCandidate candidate, string target)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        return new DependencyUpdate
        {
            OrganizationId = candidate.OrganizationId,
            ProjectId = candidate.ProjectId,
            GitInternalRepoId = candidate.RepositoryId,
            VulnerabilityMatchId = candidate.MatchId,
            Ecosystem = candidate.Ecosystem,
            PackageName = candidate.PackageName,
            FromVersion = candidate.Version,
            TargetVersion = target,
            Advisory = candidate.Advisory,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static string Truncate(string text) => text.Length <= MaxReasonLength ? text : text[..MaxReasonLength];
}

/// <summary>Which pipelines of a project are its CI and its QA: by the template they extend, else by the name the setup
/// wizard gives them (<c>&lt;prefix&gt;-ci</c>, <c>&lt;prefix&gt;-qa</c>, never <c>-qa-extended</c>). The first by id when several match.
/// There is no candidate role on purpose: an update never runs one.</summary>
internal static class DependencyUpdateRoles
{
    public static DependencyUpdatePipeline? Ci(IReadOnlyList<DependencyUpdatePipeline> pipelines) =>
        Find(pipelines, DeliveryTemplateNames.Ci, "-ci");

    public static DependencyUpdatePipeline? Qa(IReadOnlyList<DependencyUpdatePipeline> pipelines) =>
        Find(pipelines, DeliveryTemplateNames.Qa, "-qa");

    private static DependencyUpdatePipeline? Find(IReadOnlyList<DependencyUpdatePipeline> pipelines, string template, string suffix) =>
        pipelines.FirstOrDefault(pipeline => DeliveryTemplateNames.Is(pipeline.TemplateReferenceName, template))
        ?? pipelines.FirstOrDefault(pipeline => string.IsNullOrWhiteSpace(pipeline.TemplateReferenceName)
                                                && pipeline.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
}
