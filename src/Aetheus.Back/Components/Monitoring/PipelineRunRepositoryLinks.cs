// SPDX-License-Identifier: EUPL-1.2
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Components.Monitoring;

/// <summary>Resolves internal repository clone paths in one query so list entries can link to their
/// Aetheus pages, ignoring environment-dependent hosts. Invalid mirror paths or unknown
/// project/slug pairs remain unresolved.</summary>
public static class PipelineRunRepositoryLinks
{
    /// <summary>Completes <see cref="PipelineRunDto.RepositoryId"/> on list-projected runs.</summary>
    public static async Task<List<PipelineRunDto>> ApplyAsync(
        AppDbContext db, List<PipelineRunDto> runs, CancellationToken ct)
    {
        var ids = await ResolveAsync(db, runs.Where(run => run.RepositoryId is null).Select(run => run.RepositoryUrl), ct)
            .ConfigureAwait(false);
        if (ids.Count == 0) return runs;
        return runs.Select(run =>
            run.RepositoryId is null && Find(ids, run.RepositoryUrl) is { } repositoryId
                ? run with { RepositoryId = repositoryId }
                : run).ToList();
    }

    /// <summary>The repository id of every internal clone URL among <paramref name="repositoryUrls"/>
    /// that names a repository, keyed by its (project, slug) pair; read back with <see cref="Find"/>.</summary>
    public static async Task<IReadOnlyDictionary<MirrorPath, int>> ResolveAsync(
        AppDbContext db, IEnumerable<string?> repositoryUrls, CancellationToken ct)
    {
        var paths = repositoryUrls.Select(TryParse).OfType<MirrorPath>().Distinct().ToList();
        if (paths.Count == 0) return new Dictionary<MirrorPath, int>();

        var projectIds = paths.Select(path => path.ProjectId).Distinct().ToList();
        var repositories = await db.GitInternalRepos.AsNoTracking()
            .Where(repository => projectIds.Contains(repository.ProjectId))
            .Select(repository => new { repository.Id, repository.ProjectId, repository.Slug })
            .ToListAsync(ct).ConfigureAwait(false);
        return repositories.ToDictionary(
            repository => new MirrorPath(repository.ProjectId, repository.Slug),
            repository => repository.Id);
    }

    public static int? Find(IReadOnlyDictionary<MirrorPath, int> ids, string? repositoryUrl) =>
        TryParse(repositoryUrl) is { } path && ids.TryGetValue(path, out var repositoryId) ? repositoryId : null;

    /// <summary>The (project, slug) pair of an internal clone URL, or null for any other URL. The
    /// <c>.git</c> suffix is optional here because a URL stored without it names the same repository.</summary>
    internal static MirrorPath? TryParse(string? repositoryUrl)
    {
        if (string.IsNullOrWhiteSpace(repositoryUrl)
            || !Uri.TryCreate(repositoryUrl.Trim(), UriKind.Absolute, out var uri))
            return null;
        var absolutePath = uri.AbsolutePath.TrimEnd('/');
        if (!absolutePath.EndsWith(".git", StringComparison.Ordinal)) absolutePath += ".git";
        if (!MirrorRepoPath.TryParse(absolutePath, out var normalized)) return null;

        var segments = normalized.Split('/');
        return new MirrorPath(
            int.Parse(segments[1], System.Globalization.CultureInfo.InvariantCulture),
            segments[2][..^4]);
    }

    public readonly record struct MirrorPath(int ProjectId, string Slug);
}
