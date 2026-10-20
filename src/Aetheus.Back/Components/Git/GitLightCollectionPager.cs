// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Shared;

namespace Aetheus.Back.Components.Git;

internal static class GitLightCollectionPager
{
    // The column header filters of the repository page's grids. These lists are read from git (or
    // loaded whole) and paged here, so the generic map filters them in memory, before the count.
    internal static readonly GridQueryMap<GitLightBranchDto> BranchColumns = new GridQueryMap<GitLightBranchDto>()
        .Text("name", x => x.Name)
        .Text("lastCommitSha", x => x.LastCommitSha)
        .Date("lastCommitDate", x => x.LastCommitDate);

    internal static readonly GridQueryMap<GitLightTagDto> TagColumns = new GridQueryMap<GitLightTagDto>()
        .Text("name", x => x.Name)
        .Text("sha", x => x.Sha)
        .Text("message", x => x.Message)
        .Date("taggerDate", x => x.TaggerDate);

    internal static readonly GridQueryMap<GitLightTreeEntryDto> TreeColumns = new GridQueryMap<GitLightTreeEntryDto>()
        .Text("name", x => x.Name)
        .Number("size", x => x.Size);

    internal static readonly GridQueryMap<BranchProtectionRuleDto> ProtectionColumns = new GridQueryMap<BranchProtectionRuleDto>()
        .Text("pattern", x => x.Pattern)
        .Boolean("preventDeletion", x => x.PreventDeletion)
        .Boolean("preventForcePush", x => x.PreventForcePush)
        .Enum("protectionLevel", x => x.ProtectionLevel);

    public static PaginatedResult<GitLightBranchDto> Branches(
        IEnumerable<GitLightBranchDto> source, PaginationRequest request) =>
        Page(Filter(BranchColumns.ApplyFilters(source.AsQueryable(), request.Filters), request.Search, x => x.Name), request,
            x => x.Name, x => x.LastCommitDate);

    public static PaginatedResult<GitLightTagDto> Tags(
        IEnumerable<GitLightTagDto> source, PaginationRequest request) =>
        Page(Filter(TagColumns.ApplyFilters(source.AsQueryable(), request.Filters), request.Search, x => $"{x.Name} {x.Message}"), request,
            x => x.Name, x => x.TaggerDate);

    public static PaginatedResult<GitLightTreeEntryDto> Tree(
        IEnumerable<GitLightTreeEntryDto> source, PaginationRequest request) =>
        Page(Filter(TreeColumns.ApplyFilters(source.AsQueryable(), request.Filters), request.Search, x => x.Name), request,
            x => x.Name, x => x.Size);

    public static PaginatedResult<BranchProtectionRuleDto> Protection(
        IEnumerable<BranchProtectionRuleDto> source, PaginationRequest request) =>
        Page(Filter(ProtectionColumns.ApplyFilters(source.AsQueryable(), request.Filters), request.Search, x => x.Pattern), request,
            x => x.Pattern, x => x.Id);

    private static IEnumerable<T> Filter<T>(
        IEnumerable<T> source, string? search, Func<T, string?> text) =>
        string.IsNullOrWhiteSpace(search)
            ? source
            : source.Where(item => text(item)?.Contains(
                search, StringComparison.OrdinalIgnoreCase) == true);

    private static PaginatedResult<T> Page<T, TSecondary>(
        IEnumerable<T> source, PaginationRequest request,
        Func<T, string> name, Func<T, TSecondary> secondary)
    {
        var (page, pageSize) = request.Normalize();
        var items = source.ToList();
        var ordered = request.SortBy switch
        {
            "LastCommitDate" or "TaggerDate" or "Size" or "Id" =>
                request.SortDescending
                    ? items.OrderByDescending(secondary)
                    : items.OrderBy(secondary),
            _ => request.SortDescending
                ? items.OrderByDescending(name, StringComparer.OrdinalIgnoreCase)
                : items.OrderBy(name, StringComparer.OrdinalIgnoreCase)
        };
        return new PaginatedResult<T>
        {
            Items = ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList(),
            TotalCount = items.Count,
            Page = page,
            PageSize = pageSize
        };
    }
}
