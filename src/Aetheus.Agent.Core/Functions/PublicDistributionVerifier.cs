// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The <c>--verify</c> mode of <c>scripts/export-public-distribution.mjs</c>: which recent commit of the
/// internal branch the public checkout is the distribution of, if any. Every line the script printed is logged
/// as it printed it (the refusal and the closest commit at <see cref="TaskLogLevel.Warning"/>, where the script
/// wrote them to stderr and exited 1); the outcome is the matched commit, or null.
/// </summary>
internal sealed class PublicDistributionVerifier(
    PublicDistributionGit git, PublicDistributionSource source, PipelineFunctionContext context, string workspace)
{
    private const int ShownDifferences = 40;
    private const string BoundaryPath = "deploy/public-distribution/boundary.json";

    /// <summary>The commit of <paramref name="branch"/> the public checkout is the distribution of, or null
    /// when it is the distribution of none of the last <paramref name="depth"/> commits before its
    /// publication.</summary>
    public async Task<string?> VerifyAsync(
        string publicRoot, string branch, int depth, IReadOnlyList<string> ignore, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ignore);
        var publishedCommit = await git.RunAsync(publicRoot, cancellationToken, "rev-parse", "HEAD").ConfigureAwait(false);
        var published = await source.ReadPublishedAsync(publicRoot, cancellationToken).ConfigureAwait(false);
        var publishedAt = await PublishedAtAsync(publicRoot, ignore, cancellationToken).ConfigureAwait(false);
        // The candidates are the commits of the branch made before the publication, not its latest ones:
        // main follows every develop commit, so thirty commits can land between two publications and a
        // version published last week would fall out of a window counted from today.
        var commits = (await git.RunAsync(
                workspace, cancellationToken, "rev-list", $"--max-count={depth}", $"--before={publishedAt}", branch).ConfigureAwait(false))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (commits.Length == 0)
            throw new PublicDistributionException($"branch '{branch}' has no commit made before {publishedAt}");

        Closest? closest = null;
        foreach (var commit in commits)
        {
            var differences = await CompareAsync(commit, published, ignore, cancellationToken).ConfigureAwait(false);
            if (differences is null)
                continue;
            if (differences.Count == 0)
            {
                await context.Log($"Verified: public commit {publishedCommit} is the distribution of {branch} commit {commit}.").ConfigureAwait(false);
                return commit;
            }
            await context.Log($"{commit[..9]}: {differences.Count} difference(s)").ConfigureAwait(false);
            if (closest is null || differences.Count < closest.Differences.Count)
                closest = new Closest(commit, differences);
        }
        await RefuseAsync(publishedCommit, branch, commits.Length, publishedAt, closest).ConfigureAwait(false);
        return null;
    }

    /// <summary>Dated by the last public commit that touched what is compared: an edit of the README on GitHub
    /// must not move the window past the commit the code came from.</summary>
    private async Task<string> PublishedAtAsync(string publicRoot, IReadOnlyList<string> ignore, CancellationToken cancellationToken)
    {
        string[] comparedPaths = [".", .. ignore.Select(pattern => $":(exclude,glob){pattern}")];
        var compared = await git.RunAsync(publicRoot, cancellationToken, ["log", "-1", "--format=%cI", "HEAD", "--", .. comparedPaths]).ConfigureAwait(false);
        return compared.Trim().Length > 0
            ? compared.Trim()
            : (await git.RunAsync(publicRoot, cancellationToken, "log", "-1", "--format=%cI", "HEAD").ConfigureAwait(false)).Trim();
    }

    /// <summary>The differences between the published tree and the distribution of one commit, or null when
    /// that commit is not a candidate (no boundary.json, or an export that would be refused), after logging
    /// why.</summary>
    private async Task<IReadOnlyList<string>?> CompareAsync(
        string commit, IReadOnlyDictionary<string, byte[]> published, IReadOnlyList<string> ignore, CancellationToken cancellationToken)
    {
        var json = await git.TryRunAsync(workspace, cancellationToken, "show", $"{commit}:{BoundaryPath}").ConfigureAwait(false);
        var reading = json is null ? BoundaryReading.Unreadable : PublicDistributionBoundary.Read(json);
        if (reading.IsUnreadable)
        {
            await context.Log($"{commit[..9]}: no boundary.json, not a publishable commit").ConfigureAwait(false);
            return null;
        }
        var distribution = reading.Boundary is { } boundary
            ? await source.BuildAsync(boundary, commit, cancellationToken).ConfigureAwait(false)
            : PublicDistribution.Refused(reading.Problem!);
        if (distribution.Refusal is { } refusal)
        {
            await context.Log($"{commit[..9]}: its export would be refused ({refusal.Split('\n')[0]})").ConfigureAwait(false);
            return null;
        }
        return PublicDistributionBuilder.Compare(distribution.Contents, published, ignore);
    }

    private async Task RefuseAsync(string publishedCommit, string branch, int count, string publishedAt, Closest? closest)
    {
        await context.Log(
            $"Refused: public commit {publishedCommit} is the distribution of none of the last {count} commit(s) of {branch} before its publication ({publishedAt}).",
            TaskLogLevel.Warning).ConfigureAwait(false);
        if (closest is null)
            return;
        await context.Log($"Closest: {closest.Commit}, {closest.Differences.Count} difference(s):", TaskLogLevel.Warning).ConfigureAwait(false);
        foreach (var difference in closest.Differences.Take(ShownDifferences))
            await context.Log($"  {difference}", TaskLogLevel.Warning).ConfigureAwait(false);
    }

    private sealed record Closest(string Commit, IReadOnlyList<string> Differences);
}
