// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Git;

/// <summary>
/// Recette R-224 / R-226: the repository page's server-paged grids, their header filters and the values
/// those filters offer. The filters are kept so a reload after an action or a live event (page 1, no grid
/// arguments) still honours what the headers show; a live event refreshes a grid in place once it exists.
/// </summary>
internal sealed class GitRepositoryGrids
{
    public AetheusDataGrid<GitLightCommitDto>? Commits { get; set; }
    public AetheusDataGrid<GitLightBranchDto>? Branches { get; set; }
    public AetheusDataGrid<GitLightTagDto>? Tags { get; set; }
    public AetheusDataGrid<InternalPullRequestDto>? PullRequests { get; set; }
    public AetheusDataGrid<GitLightTreeEntryDto>? Tree { get; set; }
    public AetheusDataGrid<BranchProtectionRuleDto>? Protection { get; set; }

    public List<GridFilter> CommitFilters { get; set; } = [];
    public List<GridFilter> BranchFilters { get; set; } = [];
    public List<GridFilter> TagFilters { get; set; } = [];
    public List<GridFilter> TreeFilters { get; set; } = [];
    public List<GridFilter> PullRequestFilters { get; set; } = [];
    public List<GridFilter> ProtectionFilters { get; set; } = [];

    public GitRepositoryDetailFilterValuesDto Values { get; set; } = new();

    /// <summary>R2-004: the commits grid's Message column is git's --grep, which only answers "contains".</summary>
    public static IReadOnlyList<OmniDataGridFilterOperator> MessageOperators { get; } = [OmniDataGridFilterOperator.Contains];

    /// <summary>R2-004: a <c>?search=</c> link's text as the commits grid's Message filter (the column shows it
    /// as its default filter), or no filter without one.</summary>
    public static List<GridFilter> MessageFilter(string? search) => string.IsNullOrWhiteSpace(search)
        ? []
        : [new GridFilter { Field = nameof(GitLightCommitDto.Message), Operator = GridFilterOperator.Contains, Value = search }];

    private Func<string, string>? _yesNoText;
    private Func<string, string>? _prStatusText;

    /// <summary>The filter texts, built once so the columns see the same delegate on every render.</summary>
    public Func<string, string> YesNoText(IStringLocalizer localizer) => _yesNoText ??= GridFilterText.YesNo(localizer);

    public Func<string, string> PrStatusText(IStringLocalizer localizer) =>
        _prStatusText ??= GridFilterText.ForEnum<PullRequestStatus>(localizer);

    private Func<string, string>? _protectionLevelText;
    private List<OmniOption<BranchProtectionLevel>>? _protectionLevels;

    /// <summary>Decision D13: the protection level filter texts and the options of the level cell.</summary>
    public Func<string, string> ProtectionLevelText(IStringLocalizer localizer) =>
        _protectionLevelText ??= GridFilterText.ForEnum<BranchProtectionLevel>(localizer);

    public IReadOnlyList<OmniOption<BranchProtectionLevel>> ProtectionLevels(IStringLocalizer localizer) =>
        _protectionLevels ??= BranchProtectionLevelOptions.Build(localizer);

    /// <summary>Loads the values the Author filters offer, kept only while the page still shows that repository.</summary>
    public async Task LoadValuesAsync(GitApi git, int repoId, Func<int> currentRepoId)
    {
        var values = await git.GetGitRepoDetailFilterValuesAsync(repoId);
        if (currentRepoId() == repoId) Values = values;
    }

    /// <summary>The grid fetches its current page again, rows kept on screen; before the grid exists (its
    /// tab never shown) the page-1 load, which reads the kept filters, stands in.</summary>
    public static Task RefreshOrLoadAsync<T>(AetheusDataGrid<T>? grid, Func<Task> load) where T : notnull =>
        grid is not null ? grid.Refresh() : load();

    /// <summary>
    /// Recette R-327: after a user action (another ref, a search, a folder, a row created or deleted) a
    /// mounted grid reloads from its first row through its own loader, sort and header filters kept. The
    /// grids scroll (remote virtualization) and ignore rows the page fetched on its own, so a direct load
    /// alone would leave the blocks fetched before on screen. Before the grid exists the direct load stands in.
    /// </summary>
    public static Task ReloadOrLoadAsync<T>(AetheusDataGrid<T>? grid, Func<Task> load) where T : notnull =>
        grid is not null ? grid.GoToPage(0, forceReload: true) : load();

    /// <summary>The whole tab strip unmounts while the page loads a repository (its loader replaces it):
    /// the references would otherwise point at disposed grids until each tab is shown again.</summary>
    public void ForgetMountedGrids()
    {
        Commits = null;
        Branches = null;
        Tags = null;
        PullRequests = null;
        Tree = null;
        Protection = null;
    }
}
