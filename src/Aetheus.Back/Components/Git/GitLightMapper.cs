// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Git;

/// <summary>
/// Entity → DTO mappers for <see cref="GitLightService"/>, extracted to a real collaborator
/// (not a <c>partial</c> split) to keep the service within the per-file size budget.
/// <c>partial</c> here is required only for the <c>[GeneratedRegex]</c> source generator.
/// </summary>
internal static partial class GitLightMapper
{
    // Clone URL is resolved by the service (it depends on injected configuration), so it is
    // passed in rather than computed here.
    public static GitLightRepoDto MapRepoToDto(GitInternalRepo r, string cloneUrl) => new()
    {
        Id = r.Id,
        ProjectId = r.ProjectId,
        ProjectName = r.Project?.Name,
        Name = r.Name,
        Slug = r.Slug,
        Description = r.Description,
        DefaultBranch = r.DefaultBranch,
        IsEmpty = r.IsEmpty,
        CloneUrl = cloneUrl,
        CreatedAt = r.CreatedAt,
        LastPushAt = r.LastPushAt,
        IsAdditionalSource = IsAdditionalSource(r)
    };

    /// <summary>
    /// The mirror of an external repository that is not the project's own source (recette R-534): it
    /// sits beside the project's repository for the pipelines that name it. A repository whose
    /// connection was not loaded reads as the project's own, which is the behaviour before mirrors.
    /// </summary>
    public static bool IsAdditionalSource(GitInternalRepo repo) =>
        repo.GitConnection is { ProviderType: not GitProviderType.AetheusGit } connection
        && repo.Project?.GitConnectionId != connection.Id;

    /// <summary>Translates a pull-request sort key from the DTO's vocabulary to the entity's.
    /// <para>The grid sends the DTO property name, and the DTO's <c>CreatedAt</c> is the entity's
    /// <see cref="PullRequest.ExternalCreatedAt"/> (when the PR was opened upstream). Passing the name
    /// through untranslated would order by the row's own audit <c>CreatedAt</c> instead: a real column,
    /// so no error, just an order that disagrees with the dates on screen. It lives beside the mapping
    /// it mirrors, so the two cannot drift apart.</para></summary>
    public static string? MapPrSortKey(string? sortBy) =>
        string.Equals(sortBy, nameof(InternalPullRequestDto.CreatedAt), StringComparison.OrdinalIgnoreCase)
            ? nameof(PullRequest.ExternalCreatedAt)
            : sortBy;

    public static InternalPullRequestDto MapPrToDto(PullRequest p) => new()
    {
        Id = p.Id,
        Number = p.ExternalId,
        Title = p.Title,
        Description = p.Description,
        SourceBranch = p.SourceBranch,
        TargetBranch = p.TargetBranch,
        AuthorLogin = p.AuthorLogin,
        Status = p.Status,
        HeadCommitSha = p.HeadCommitSha,
        MergeCommitSha = p.MergeCommitSha,
        CreatedAt = p.ExternalCreatedAt,
        MergedAt = p.ExternalMergedAt
    };

    public static string GenerateSlug(string name)
    {
        var slug = name.ToLowerInvariant().Trim();
        slug = SlugInvalidChars().Replace(slug, "-");
        slug = SlugMultipleDashes().Replace(slug, "-");
        return slug.Trim('-');
    }

    public static BranchProtectionRuleDto MapProtectionRuleToDto(BranchProtectionRule r) => new()
    {
        Id = r.Id,
        GitInternalRepoId = r.GitInternalRepoId,
        Pattern = r.Pattern,
        PreventDeletion = r.PreventDeletion,
        PreventForcePush = r.PreventForcePush,
        ProtectionLevel = r.ProtectionLevel,
        CreatedAt = r.CreatedAt
    };

    [GeneratedRegex(@"[^a-z0-9\-]")]
    private static partial Regex SlugInvalidChars();

    [GeneratedRegex(@"-{2,}")]
    private static partial Regex SlugMultipleDashes();
}
