// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// Reads trees out of Git for the verification: the tree of a revision of the internal repository, turned
/// into the distribution it would be exported as (<c>buildDistribution</c> of
/// <c>scripts/export-public-distribution.mjs</c>), and the tree of the public checkout's
/// <c>HEAD</c> as it was published. Each tree goes through one archive file in the step's temporary folder,
/// listed first, so an export that would be refused for an unlisted top-level entry never reads a file.
/// </summary>
internal sealed class PublicDistributionSource(PublicDistributionGit git, string workspace, string temporaryDirectory)
{
    private string ArchivePath => Path.Combine(temporaryDirectory, "export.tar");

    /// <summary>The distribution of a revision of the workspace's repository, or the reason its export
    /// would be refused.</summary>
    public async Task<PublicDistribution> BuildAsync(PublicDistributionBoundary boundary, string revision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(boundary);
        await git.ArchiveAsync(workspace, revision, ArchivePath, cancellationToken).ConfigureAwait(false);
        var files = await PublicDistributionArchive.ListAsync(ArchivePath, cancellationToken).ConfigureAwait(false);
        var plan = PublicDistributionPlan.Create(files, boundary);
        if (plan.Refusal is not null)
            return PublicDistribution.Refused(plan.Refusal);
        var unlisted = PublicDistributionPlan.UnlistedTopLevel(plan.Copies.Keys, boundary.TopLevel);
        if (unlisted.Count > 0)
            return PublicDistribution.Refused(
                $"top-level entries that are neither listed nor excluded in boundary.json: {string.Join(", ", unlisted)}");
        var tree = await PublicDistributionArchive
            .ReadAsync(ArchivePath, plan.Copies.Values.ToHashSet(StringComparer.Ordinal), cancellationToken).ConfigureAwait(false);
        return PublicDistributionBuilder.Assemble(plan.Copies, tree, boundary);
    }

    /// <summary>Every file of <c>HEAD</c> of the public checkout, path to bytes.</summary>
    public async Task<Dictionary<string, byte[]>> ReadPublishedAsync(string publicRoot, CancellationToken cancellationToken)
    {
        await git.ArchiveAsync(publicRoot, "HEAD", ArchivePath, cancellationToken).ConfigureAwait(false);
        return await PublicDistributionArchive.ReadAsync(ArchivePath, wanted: null, cancellationToken).ConfigureAwait(false);
    }
}
