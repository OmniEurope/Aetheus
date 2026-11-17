// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis.DependencyBump;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Git.Events;
using Aetheus.Back.Components.Inventory;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Analysis;

/// <summary>What the repository gave for one bump.</summary>
internal sealed record CveRemediationPlan(DependencyBumpResult? Result, string? BaseCommit, string? Failure);

/// <summary>
/// What a remediation reads from and writes to a repository, always in the requester's name once the caller's rights were checked.
/// The back writes the branch itself: the run that refreshes a lock file never holds a right to push (<see cref="GitRunCloneToken"/>
/// only reads). Only a branch <see cref="CveRemediationBranch"/> or <see cref="DependencyUpdateBranch"/> named is ever written, and never one a protection rule covers,
/// nor the default branch of the repository.
/// </summary>
internal sealed class CveRemediationGit(
    IProjectSourceReader source,
    IGitLightService git,
    IGitLightCliService cli,
    IGitLightRepository repositories,
    IGitSmartHttpService smartHttp,
    CveRemediationRepository remediations)
{
    private const int MaxFiles = 2000;
    private const int MaxFileBytes = 4 * 1024 * 1024;

    private static readonly HashSet<string> NuGetFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Directory.Packages.props", "Directory.Build.props", "Directory.Build.targets"
    };

    private static readonly HashSet<string> LockFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "packages.lock.json", "package-lock.json", "npm-shrinkwrap.json", "pnpm-lock.yaml", "yarn.lock", "composer.lock"
    };

    private static readonly string[] InstalledFolders = ["node_modules/", "vendor/", "bin/", "obj/"];

    /// <summary>The files of the base branch this bump can touch, then the plan over them. A file too large to be a manifest fails the read.</summary>
    public async Task<CveRemediationPlan> PlanAsync(int repositoryId, string baseBranch, DependencyBumpRequest request, CancellationToken ct)
    {
        var tree = await source.ReadTreeAsync(repositoryId, baseBranch, ct).ConfigureAwait(false);
        if (tree is null) return new CveRemediationPlan(null, null, $"Branch '{baseBranch}' cannot be read.");
        var relevant = tree.Files.Where(file => IsRelevant(request, file.Path)).Take(MaxFiles + 1).ToList();
        if (relevant.Count > MaxFiles) return new CveRemediationPlan(null, null, "The repository holds too many manifests to plan a bump.");

        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in relevant)
        {
            // Only the presence of a lock file matters to the plan, whose text can be huge.
            if (LockFiles.Contains(NameOf(file.Path)))
            {
                files[file.Path] = string.Empty;
                continue;
            }

            var bytes = await ReadAsync(repositoryId, tree.CommitSha, file.Path, ct).ConfigureAwait(false);
            if (bytes is null) return new CveRemediationPlan(null, null, $"'{file.Path}' cannot be read, or is too large to be a manifest.");
            files[file.Path] = System.Text.Encoding.UTF8.GetString(bytes);
        }

        return new CveRemediationPlan(DependencyBumper.Plan(request, files), tree.CommitSha, null);
    }

    /// <summary>The content of a file at a revision, or null when it is missing or above the cap.</summary>
    public async Task<byte[]?> ReadAsync(int repositoryId, string revision, string path, CancellationToken ct)
    {
        await using var stream = await source.OpenFileAsync(repositoryId, revision, path, ct).ConfigureAwait(false);
        if (stream is null) return null;
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxFileBytes) return null;
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>Why this branch may not be written, or null when it may. Rules are read again before every write. Only a branch of a
    /// remediation (<see cref="CveRemediationBranch"/>) or of an automatic dependency update (<see cref="DependencyUpdateBranch"/>, PLAN-007).</summary>
    public async Task<string?> WriteRefusalAsync(GitInternalRepo repository, string branch, CancellationToken ct)
    {
        if (!(CveRemediationBranch.IsOurs(branch) || DependencyUpdateBranch.IsOurs(branch))
            || string.Equals(branch, repository.DefaultBranch, StringComparison.Ordinal))
            return $"'{branch}' is not a branch a remediation may write.";
        var rules = await repositories.GetBranchProtectionRulesAsync(repository.Id, ct).ConfigureAwait(false);
        return GitBranchWritePolicy.RefusalReason(branch, rules, GitBranchWriter.HumanPush);
    }

    /// <summary>Puts <paramref name="files"/> on the branch, created from <paramref name="baseCommit"/> if it is new, as the requester, then
    /// announces it as a push so that the project's CI follows. Returns the error, or null.</summary>
    public async Task<string?> PushAsync(
        GitInternalRepo repository, string branch, string baseCommit, IReadOnlyList<(string Path, string Content)> files,
        string message, string requester, CancellationToken ct)
    {
        if (await WriteRefusalAsync(repository, branch, ct).ConfigureAwait(false) is { } refusal) return refusal;
        var diskPath = git.ResolveDiskPath(repository.ProjectId, repository.Slug);
        var previous = await TipAsync(diskPath, branch, ct).ConfigureAwait(false);
        if (previous is null) await cli.CreateBranchAsync(diskPath, branch, baseCommit, ct).ConfigureAwait(false);
        var (success, commit, error) = await cli.CommitFilesAsync(diskPath, branch, files, message, requester, $"{requester}@aetheus", ct).ConfigureAwait(false);
        if (!success) return error ?? "The commit failed.";
        if (commit is not null && commit != previous)
        {
            await smartHttp.MarkPushedAsync(
                repository.ProjectId, repository.Slug, [new GitRefUpdate($"refs/heads/{branch}", commit, previous ?? string.Empty)], ct, requester)
                .ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>Opens the pull request in the requester's name, or finds the one already open from this branch. Merging stays human.</summary>
    public async Task<int> OpenPullRequestAsync(VulnerabilityRemediation remediation, GitInternalRepo repository, string title, string description, CancellationToken ct)
    {
        var branch = remediation.Branch!;
        var target = remediation.BaseBranch!;
        if (repository.GitConnectionId is { } connection
            && await remediations.FindOpenPullRequestAsync(connection, branch, target, ct).ConfigureAwait(false) is { } open)
            return open.Id;
        var created = await git.CreatePullRequestAsync(
            repository.Id,
            new CreateInternalPullRequestRequest { SourceBranch = branch, TargetBranch = target, Title = title, Description = description },
            remediation.RequestedByUsername, ct).ConfigureAwait(false);
        return created.Id;
    }

    private async Task<string?> TipAsync(string diskPath, string branch, CancellationToken ct)
    {
        var branches = await cli.GetBranchesAsync(diskPath, string.Empty, ct).ConfigureAwait(false);
        if (!branches.Exists(candidate => candidate.Name == branch)) return null;
        var commits = await cli.GetCommitsAsync(diskPath, branch, 0, 1, ct: ct).ConfigureAwait(false);
        return commits.Count == 0 ? null : commits[0].Sha;
    }

    private static bool IsRelevant(DependencyBumpRequest request, string path)
    {
        if (string.Equals(path, request.SourcePath, StringComparison.Ordinal)) return true;
        if (InstalledFolders.Any(folder => path.StartsWith(folder, StringComparison.Ordinal) || path.Contains("/" + folder, StringComparison.Ordinal))) return false;
        var name = NameOf(path);
        return request.Ecosystem switch
        {
            "NuGet" => NuGetFiles.Contains(name) || LockFiles.Contains(name) || name.EndsWith("proj", StringComparison.OrdinalIgnoreCase),
            "npm" => name.Equals("package.json", StringComparison.OrdinalIgnoreCase) || LockFiles.Contains(name),
            "Packagist" => name.Equals("composer.json", StringComparison.OrdinalIgnoreCase) || LockFiles.Contains(name),
            "runtime" => LockFiles.Contains(name),
            _ => false
        };
    }

    private static string NameOf(string path) => path[(path.LastIndexOf('/') + 1)..];
}
