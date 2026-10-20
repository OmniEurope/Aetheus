// SPDX-License-Identifier: EUPL-1.2
using System.Text;

namespace Aetheus.Back.Components.Git;

/// <summary> Downloads a hosted repository as a zip. The archive's top folder is the repository
/// slug, and the ref defaults to the repository's default branch. </summary>
public sealed class GitArchiveService(IGitLightRepository lightRepo, IGitLightService light, IGitLightCliService cli)
{
    public async Task<GitArchive?> GetArchiveAsync(int repoId, string? refName, CancellationToken ct = default)
    {
        var entity = await lightRepo.FindByIdAsync(repoId, ct).ConfigureAwait(false);
        if (entity is null) return null;

        var reference = string.IsNullOrWhiteSpace(refName) ? entity.DefaultBranch : refName.Trim();
        // The CLI refuses a ref git would read as an option (EnsureRefArgsSafe); say so as a 400 here
        // rather than letting its ArgumentException surface as a 500.
        if (reference.StartsWith('-'))
            throw new BadRequestException($"Ref may not start with '-': '{reference}'.");
        var stream = await cli.GetArchiveStreamAsync(
            light.ResolveDiskPath(entity.ProjectId, entity.Slug), reference, entity.Slug, ct).ConfigureAwait(false);
        return stream is null ? null : new GitArchive(stream, $"{entity.Slug}-{FileNamePart(reference)}.zip");
    }

    // A ref such as "feature/x" or "v1.0" becomes a file-name-safe part ("feature-x", "v1.0").
    private static string FileNamePart(string reference)
    {
        var part = new StringBuilder(reference.Length);
        foreach (var character in reference)
            part.Append(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' ? character : '-');
        return part.ToString();
    }
}
