// SPDX-License-Identifier: EUPL-1.2
using System.IO.Compression;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using Aetheus.Back.Components.Analysis.DependencyBump;
using Aetheus.Back.Components.Artifacts;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Analysis;

/// <summary>Whether a lock file can be refreshed here: the pipeline, or the reason it cannot.</summary>
internal sealed record CveRemediationRunReadiness(int? PipelineId, CveRemediationFallbackReason? Refusal, string? Detail);

/// <summary>What the run handed back: the lock files by path, or why they cannot be used.</summary>
internal sealed record CveRemediationLocks(IReadOnlyList<(string Path, string Content)> Files, string? Failure);

/// <summary>
/// The refresh of a lock file, which only its package manager can write. The run is the project's own pipeline
/// <see cref="PipelineName"/>, launched on the branch of the fix with the command of the plan in its parameter
/// <see cref="CommandParameter"/>, and a later stage of that pipeline (steps of one stage run in parallel, ADR-012)
/// declares the lock files as the artifact <see cref="ArtifactName"/>: the run reads and computes, the back commits
/// what it handed back. So a run never needs a right to push, and it runs under the rights of the
/// requester: <c>pipeline.run</c> and Admin on every server it targets, as a launch by hand. A project without that pipeline gets the
/// command to run itself, which is the honest answer for a project without a runner too.
/// </summary>
internal sealed partial class CveRemediationLockRun(
    CveRemediationRepository repository,
    IPipelineRunService runs,
    IPipelineArtifactService artifacts,
    IArtifactService artifactFiles,
    IResourceAuthorizationService authz)
{
    public const string PipelineName = "cve-lock-refresh";
    public const string ArtifactName = "dependency-locks";
    public const string CommandParameter = "LOCK_COMMAND";

    private const long MaxLockBytes = 32 * 1024 * 1024;

    /// <summary>Checked before anything is written: a branch whose lock file nobody can refresh is not left behind.</summary>
    public async Task<CveRemediationRunReadiness> CheckAsync(ClaimsPrincipal user, int projectId, string baseBranch, CancellationToken ct)
    {
        if (await repository.FindPipelineAsync(projectId, PipelineName, ct).ConfigureAwait(false) is not { } pipelineId)
        {
            return new CveRemediationRunReadiness(null, CveRemediationFallbackReason.NoRefreshPipeline,
                $"The project has no pipeline named '{PipelineName}' to run the package manager.");
        }

        if (!await authz.HasPermissionAsync(user, PermissionKeys.PipelineRun, pipelineId, ct).ConfigureAwait(false))
            return MissingRights(PipelineName);
        var preparation = await runs.PrepareRunAsync(pipelineId, baseBranch, ct).ConfigureAwait(false);
        if (preparation is null)
            return new CveRemediationRunReadiness(null, CveRemediationFallbackReason.NoRefreshPipeline, $"The pipeline '{PipelineName}' cannot be prepared.");
        return await AdministersAsync(user, preparation.TargetServerIds, ct).ConfigureAwait(false)
            ? new CveRemediationRunReadiness(pipelineId, null, null)
            : MissingRights("the servers it targets");
    }

    private static CveRemediationRunReadiness MissingRights(string what) =>
        new(null, CveRemediationFallbackReason.RunRightsMissing, $"The requester may not launch {what}.");

    private async Task<bool> AdministersAsync(ClaimsPrincipal user, IReadOnlyCollection<int> servers, CancellationToken ct)
    {
        foreach (var server in servers)
        {
            if (!await authz.HasPermissionAsync(user, ResourceType.Server, server, Permission.Admin, ct).ConfigureAwait(false)) return false;
        }

        return true;
    }

    /// <summary>The one shell line of the plan's commands, or null when a directory holds anything but plain path characters.
    /// The commands themselves are built by the bump from validated names.</summary>
    public static string? CommandLine(IReadOnlyList<DependencyBumpLockRefresh> refreshes)
    {
        if (refreshes.Any(refresh => !PlainDirectory().IsMatch(refresh.WorkingDirectory))) return null;
        return string.Join(" && ", refreshes.Select(refresh =>
            refresh.WorkingDirectory.Length == 0 ? $"( {refresh.Command} )" : $"( cd {refresh.WorkingDirectory} && {refresh.Command} )"));
    }

    /// <summary>Launches the run on the branch. The exact targets of that branch are authorized again: its definition may differ from the base.
    /// The key is the attempt: a replayed key answers the run that already exists, so a retry of the match needs a key of its own.</summary>
    public async Task<int?> LaunchAsync(ClaimsPrincipal user, int pipelineId, string branch, string command, int remediationId, CancellationToken ct)
    {
        var preparation = await runs.PrepareRunAsync(pipelineId, branch, ct).ConfigureAwait(false);
        if (preparation is null) return null;
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal) { [CommandParameter] = command };
        var resolved = await runs.ResolveRunParametersAsync(preparation, parameters, ct).ConfigureAwait(false);
        if (!await AdministersAsync(user, resolved.TargetServerIds, ct).ConfigureAwait(false)) return null;
        var run = await runs.TriggerPreparedRunAsync(resolved, parameters: parameters, ct: ct, idempotencyKey: $"cve-remediation-{remediationId}")
            .ConfigureAwait(false);
        return run?.Id;
    }

    /// <summary>The lock files of the run's artifact. Every path the plan named has to be there: a part of a refresh is no refresh.</summary>
    public async Task<CveRemediationLocks> ReadLocksAsync(int runId, IReadOnlyList<string> paths, CancellationToken ct)
    {
        var artifact = (await artifacts.GetArtifactsAsync(runId, ct).ConfigureAwait(false)).FirstOrDefault(candidate => candidate.Name == ArtifactName);
        if (artifact is null) return Failed($"The run published no artifact named '{ArtifactName}'.");
        if (artifact.SizeBytes > MaxLockBytes) return Failed($"The artifact '{ArtifactName}' is too large to hold lock files.");
        await using var stream = await artifactFiles.DownloadArtifactAsync(artifact.Id, ct).ConfigureAwait(false);
        if (stream is null) return Failed($"The content of the artifact '{ArtifactName}' is not available.");
        try
        {
            await using var archive = await ZipArchive.CreateAsync(stream, ZipArchiveMode.Read, leaveOpen: false, entryNameEncoding: null, ct).ConfigureAwait(false);
            return await ReadEntriesAsync(archive, paths, ct).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            return Failed($"The artifact '{ArtifactName}' is not a readable archive.");
        }
    }

    private static async Task<CveRemediationLocks> ReadEntriesAsync(ZipArchive archive, IReadOnlyList<string> paths, CancellationToken ct)
    {
        var files = new List<(string Path, string Content)>(paths.Count);
        foreach (var path in paths)
        {
            var entry = archive.Entries.FirstOrDefault(candidate => candidate.FullName.Replace('\\', '/') == path);
            if (entry is null) return Failed($"The run did not hand back '{path}'.");
            if (entry.Length > MaxLockBytes) return Failed($"'{path}' is too large to be a lock file.");
            await using var content = await entry.OpenAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(content, Encoding.UTF8);
            files.Add((path, await reader.ReadToEndAsync(ct).ConfigureAwait(false)));
        }

        return new CveRemediationLocks(files, null);
    }

    private static CveRemediationLocks Failed(string reason) => new([], reason);

    [GeneratedRegex(@"^[A-Za-z0-9._/\-]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex PlainDirectory();
}
