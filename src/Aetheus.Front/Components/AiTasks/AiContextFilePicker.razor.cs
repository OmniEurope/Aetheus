// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.AiTasks;

/// <summary>
/// The context files of an AI task: a list of repository paths (a folder ends with <c>/</c>) and a tree of
/// the project's repository to pick folders or several files from. The tree loads one folder at a time,
/// when it opens; the business rule (at most <see cref="MaxPaths"/> relative paths) stays the backend's.
/// </summary>
public partial class AiContextFilePicker
{
    /// <summary>The backend's limit on the paths of one task (<c>AiRepositoryContextRules.MaxContextPaths</c>).</summary>
    internal const int MaxPaths = 32;

    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    /// <summary>The project whose repository the tree browses.</summary>
    [Parameter] public int? ProjectId { get; set; }

    [Parameter] public IReadOnlyList<string> Paths { get; set; } = [];
    [Parameter] public EventCallback<IReadOnlyList<string>> PathsChanged { get; set; }

    private List<GitLightRepoDto> _repos = [];
    private List<AiContextTreeNode> _roots = [];
    private IReadOnlyList<string> _picked = [];
    private int _repoId;
    private bool _open;
    private bool _loading;
    private bool _loadFailed;
    private bool _limitReached;

    private async Task OpenAsync()
    {
        _open = true;
        _picked = [];
        _loadFailed = false;
        _limitReached = false;
        _loading = true;
        try
        {
            _repos = (await Api.Git.GetGitReposAsync(ProjectId))
                .Where(repo => !repo.IsAdditionalSource && !repo.IsEmpty)
                .ToList();
            if (_repos.Count > 0)
            {
                _repoId = _repos[0].Id;
                await LoadRootAsync();
            }
        }
        catch (HttpRequestException)
        {
            _loadFailed = true;
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task OnRepoChosenAsync(int repoId)
    {
        _repoId = repoId;
        _picked = [];
        await LoadRootAsync();
    }

    private async Task LoadRootAsync()
    {
        _loadFailed = false;
        try
        {
            _roots = await ReadFolderAsync(null, CancellationToken.None);
        }
        catch (HttpRequestException)
        {
            _roots = [];
            _loadFailed = true;
        }
    }

    /// <summary>Reads the first level of a folder, folders before files.</summary>
    private async Task<List<AiContextTreeNode>> ReadFolderAsync(string? path, CancellationToken ct)
    {
        var repo = _repos.First(candidate => candidate.Id == _repoId);
        var entries = await Api.Git.GetGitTreeAsync(_repoId, repo.DefaultBranch, path, ct);
        return entries
            .OrderByDescending(entry => entry.Type == GitTreeEntryType.Tree)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entry => new AiContextTreeNode(entry))
            .ToList();
    }

    private async Task LoadChildrenAsync(AiContextTreeNode folder, CancellationToken ct)
    {
        if (folder.Children.Count > 0) return;
        folder.Children.AddRange(await ReadFolderAsync(folder.Path.TrimEnd('/'), ct));
    }

    private async Task ConfirmAsync()
    {
        var merged = Paths.ToList();
        _limitReached = false;
        foreach (var path in _picked)
        {
            if (merged.Contains(path, StringComparer.Ordinal)) continue;
            if (merged.Count >= MaxPaths)
            {
                _limitReached = true;
                break;
            }
            merged.Add(path);
        }
        await PathsChanged.InvokeAsync(merged);
        if (!_limitReached) _open = false;
    }

    private Task RemoveAsync(string path) =>
        PathsChanged.InvokeAsync(Paths.Where(existing => !string.Equals(existing, path, StringComparison.Ordinal)).ToList());

    private void Close() => _open = false;
}
