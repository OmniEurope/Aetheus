// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Git;
using Microsoft.Extensions.Caching.Memory;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>One <c>.pipeline/</c> file of a mirror, read at one commit and judged by the import policy.</summary>
internal sealed record PipelineExternalMirrorFile(
    GitLightRepoDto Repository, string CommitSha, string FilePath, string Name, string? Yaml, List<string> Problems);

/// <summary>A mirror's files at its head commit, or why the mirror could not be read. <paramref name="SkippedFiles"/>
/// counts the <c>.pipeline/</c> files left unread because the mirror holds more than the reader's cap.</summary>
internal sealed record PipelineExternalMirrorSnapshot(
    GitLightRepoDto Repository, string? CommitSha, List<PipelineExternalMirrorFile> Files, string? Unreadable, int SkippedFiles = 0);

/// <summary>
/// R-11: reads the <c>.pipeline/*.yaml</c> files of a project's external repository mirrors at the mirror's head commit,
/// and only reads: nothing here writes to a mirror (decision R-23: the external repository is never modified). Every
/// file goes through the same strict validation as a definition saved in Aetheus (size bound, structure, templates of
/// the organization's catalogue only), then through <see cref="PipelineExternalImportPolicy"/> on the effective
/// definition. The mirror is untrusted: at most <see cref="MaxFiles"/> files are read, a file larger than
/// <see cref="PipelineDefinitionLimits.MaxYamlLength"/> is refused from its tree size without being read, and every
/// file goes through <see cref="PipelineExternalYamlGuard"/> before it is parsed. A read of the listing is cached for
/// <see cref="CacheLifetime"/>, keyed by the mirror's head commit (a commit never changes); an import never uses it.
/// </summary>
internal sealed class PipelineExternalMirrorReader(
    IGitLightService gitService,
    IGitLightCliService cli,
    IPipelineService pipelineService,
    IMemoryCache cache)
{
    internal const int MaxFiles = 50;
    internal static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(30);

    private sealed record CachedRead(List<PipelineExternalMirrorFile> Files, int SkippedFiles);

    /// <summary>The project's repositories that mirror an external repository.</summary>
    public async Task<List<GitLightRepoDto>> GetMirrorsAsync(int projectId, CancellationToken ct)
        => (await gitService.GetRepositoriesAsync(projectId, ct).ConfigureAwait(false))
            .Where(repository => repository.IsExternalMirror && repository.ProjectId == projectId)
            .ToList();

    /// <param name="allowCached">True for a listing; false when the result is acted on (an import re-reads and
    /// re-validates the file itself).</param>
    public async Task<PipelineExternalMirrorSnapshot> ReadAsync(
        int projectId, GitLightRepoDto mirror, CancellationToken ct, bool allowCached = false)
    {
        ArgumentNullException.ThrowIfNull(mirror);
        if (mirror.IsEmpty)
            return new(mirror, null, [], "the mirror holds no commit yet: synchronize it from its repository page.");
        var diskPath = gitService.ResolveDiskPath(projectId, mirror.Slug);
        if (!Directory.Exists(diskPath))
            return new(mirror, null, [], "the mirror is not on the disk yet: synchronize it from its repository page.");
        var head = (await cli.GetCommitsAsync(diskPath, mirror.DefaultBranch, 0, 1, ct: ct).ConfigureAwait(false))
            .FirstOrDefault()?.Sha;
        if (head is null || !PipelineRunHelpers.IsGitCommitHash(head))
            return new(mirror, null, [], $"the branch '{mirror.DefaultBranch}' of the mirror has no readable commit.");

        var cacheKey = $"pipeline-external-mirror:{projectId}:{mirror.Id}:{head}";
        if (allowCached && cache.TryGetValue(cacheKey, out CachedRead? cached) && cached is not null)
            return new(mirror, head, cached.Files, null, cached.SkippedFiles);

        var tree = await cli.GetTreeAsync(diskPath, head, PipelineExternalImportPolicy.PipelineDirectory.TrimEnd('/'), ct)
            .ConfigureAwait(false);
        var entries = tree
            .Where(entry => entry.Type == GitTreeEntryType.Blob
                            && (entry.Name.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
                                || entry.Name.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(entry => entry.Name, StringComparer.Ordinal)
            .ToList();
        var files = new List<PipelineExternalMirrorFile>();
        foreach (var entry in entries.Take(MaxFiles))
        {
            var path = PipelineExternalImportPolicy.PipelineDirectory + entry.Name;
            files.Add(await ReadFileAsync(projectId, mirror, diskPath, head, path, entry.Size, ct).ConfigureAwait(false));
        }
        var skipped = Math.Max(0, entries.Count - MaxFiles);
        if (allowCached)
            cache.Set(cacheKey, new CachedRead(files, skipped), CacheLifetime);
        return new(mirror, head, files, null, skipped);
    }

    private async Task<PipelineExternalMirrorFile> ReadFileAsync(
        int projectId, GitLightRepoDto mirror, string diskPath, string commit, string path, long? treeSize, CancellationToken ct)
    {
        var fallbackName = Path.GetFileNameWithoutExtension(path);
        if (!PipelineExternalImportPolicy.IsSafeFilePath(path))
            return new(mirror, commit, path, fallbackName, null, ["The file name is not a plain .pipeline/*.yaml name."]);
        // The size Git lists for the tree entry: an oversized file is refused before its content is ever read.
        if (treeSize > PipelineDefinitionLimits.MaxYamlLength)
            return new(mirror, commit, path, fallbackName, null,
                [$"The file exceeds {PipelineDefinitionLimits.MaxYamlLength} characters."]);
        var blob = await cli.GetBlobAsync(diskPath, commit, path, ct).ConfigureAwait(false);
        var blobRefusal = RefuseBlob(blob);
        if (blobRefusal is not null)
            return new(mirror, commit, path, fallbackName, null, [blobRefusal]);

        var yaml = blob!.Content!;
        var (name, textRefusal) = ReadName(yaml, fallbackName);
        if (textRefusal is not null)
            return new(mirror, commit, path, fallbackName, null, [textRefusal]);

        var validation = await pipelineService.ValidateYamlStrictAsync(yaml, projectId, null, null, ct: ct).ConfigureAwait(false);
        if (!validation.IsValid || validation.Definition is null)
            return new(mirror, commit, path, name, yaml, validation.Errors.Count > 0 ? validation.Errors : ["The definition is invalid."]);
        return new(mirror, commit, path, name, yaml, PipelineExternalImportPolicy.Validate(validation.Definition));
    }

    private static string? RefuseBlob(GitLightBlobDto? blob)
    {
        if (blob is null || blob.IsBinary || string.IsNullOrWhiteSpace(blob.Content))
            return "The file is empty or not text.";
        if (blob.Size > PipelineDefinitionLimits.MaxYamlLength || blob.Content.Length > PipelineDefinitionLimits.MaxYamlLength)
            return $"The file exceeds {PipelineDefinitionLimits.MaxYamlLength} characters.";
        return null;
    }

    private static (string Name, string? Refusal) ReadName(string yaml, string fallbackName)
    {
        var refusal = PipelineExternalYamlGuard.Check(yaml);
        if (refusal is not null)
            return (fallbackName, refusal);
        var parsed = YamlParsingHelper.ParseAndValidate(yaml, out var parseError);
        if (parsed is null)
            return (fallbackName, parseError ?? "The file is not a pipeline definition.");
        var name = string.IsNullOrWhiteSpace(parsed.Name) ? fallbackName : parsed.Name.Trim();
        return name.Length > 200
            ? (fallbackName, "The pipeline name is longer than 200 characters.")
            : (name, null);
    }
}
