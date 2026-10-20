// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;
using System.Text.Json;

namespace Aetheus.Front.Components.Git;

public partial class GitCommitDetail
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private BreadcrumbService Breadcrumb { get; set; } = default!;
    [Inject] private ILogger<GitCommitDetail> Logger { get; set; } = default!;

    [Parameter] public int RepoId { get; set; }
    [Parameter] public string Sha { get; set; } = string.Empty;

    private GitLightCommitDetailDto? _detail;
    private FileDiffDto? _selectedFile;
    private string SelectedFilePath => _selectedFile?.Path ?? string.Empty;
    private IReadOnlyList<FileDiffDto> _visibleFiles = [];
    private IReadOnlyList<OmniOption<string>> FileOptions => _visibleFiles
        .Select(file => new OmniOption<string>(file.Path, $"{file.Path} (+{file.Additions} / -{file.Deletions})"))
        .ToArray();
    private IReadOnlyList<string> _availableStatuses = [];
    private IReadOnlyList<DiffLineView> _selectedDiffLines = [];
    private string _fileSearch = string.Empty;
    private string? _statusFilter;
    private bool _diffCollapsed;
    private bool _loading = true;
    private string? _loadedKey;

    protected override async Task OnParametersSetAsync()
    {
        var key = $"{RepoId}/{Sha}";
        if (_loadedKey == key) return;
        _loadedKey = key;

        _loading = true;
        ResetDiffBrowser();
        try
        {
            var detailTask = Api.Git.GetGitCommitDetailAsync(RepoId, Sha);
            var repositoryTask = Api.Git.GetGitRepoAsync(RepoId);
            await Task.WhenAll(detailTask, repositoryTask);
            _detail = await detailTask;
            InitializeDiffBrowser();
            var repository = await repositoryTask;
            if (repository is not null)
            {
                Breadcrumb.Set(
                    new BreadcrumbItem(L["Projects"], "/projects"),
                    new BreadcrumbItem(repository.ProjectName ?? $"{L["Project"]} #{repository.ProjectId}", $"/projects/{repository.ProjectId}/overview"),
                    new BreadcrumbItem(L["GitRepositories"], $"/git-repositories?projectId={repository.ProjectId}"),
                    new BreadcrumbItem(repository.Name, $"/git-repositories/{RepoId}"),
                    new BreadcrumbItem(Short(Sha)));
            }
            else
            {
                Breadcrumb.Set(
                    new BreadcrumbItem(L["GitRepositories"], "/git-repositories"),
                    new BreadcrumbItem($"{L["Repository"]} #{RepoId}", $"/git-repositories/{RepoId}"),
                    new BreadcrumbItem(Short(Sha)));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            GitLoadFailureLogger.Log(Logger, ex, "commit detail", detailPage: true);
            _detail = null; // 401 on expired JWT - redirect handled by AuthProvider
        }
        _loading = false;
    }

    private static string Short(string sha) => DisplayFormatting.ShortSha(sha);

    private void ResetDiffBrowser()
    {
        _selectedFile = null;
        _visibleFiles = [];
        _availableStatuses = [];
        _selectedDiffLines = [];
        _fileSearch = string.Empty;
        _statusFilter = null;
        _diffCollapsed = false;
    }

    private void InitializeDiffBrowser()
    {
        if (_detail is null) return;

        _availableStatuses = _detail.Diff.FileDiffs
            .Select(file => file.Status.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(StatusOrder)
            .ThenBy(status => status, StringComparer.Ordinal)
            .ToArray();
        ApplyFileFilters();
    }

    private void OnFileSearchChanged() => ApplyFileFilters();

    private void SetStatusFilter(string? status)
    {
        _statusFilter = status;
        ApplyFileFilters();
    }

    private void ApplyFileFilters()
    {
        if (_detail is null)
        {
            _visibleFiles = [];
            SelectFile(null);
            return;
        }

        _visibleFiles = _detail.Diff.FileDiffs
            .Where(file => string.IsNullOrWhiteSpace(_statusFilter)
                || string.Equals(file.Status, _statusFilter, StringComparison.OrdinalIgnoreCase))
            .Where(file => string.IsNullOrWhiteSpace(_fileSearch)
                || file.Path.Contains(_fileSearch.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (_selectedFile is null || !_visibleFiles.Contains(_selectedFile))
        {
            SelectFile(_visibleFiles.FirstOrDefault());
        }
    }

    private void OnFileSelected(object? value)
    {
        var path = value?.ToString();
        SelectFile(_visibleFiles.FirstOrDefault(file => string.Equals(file.Path, path, StringComparison.Ordinal)));
    }

    private void OnFilePathSelected(string path) => OnFileSelected(path);

    private void SelectFile(FileDiffDto? file)
    {
        _selectedFile = file;
        _selectedDiffLines = file?.Patch is { Length: > 0 } patch ? BuildDiffLines(patch) : [];
        _diffCollapsed = false;
    }

    private void ToggleDiff() => _diffCollapsed = !_diffCollapsed;

    private int StatusCount(string status) =>
        _detail?.Diff.FileDiffs.Count(file => string.Equals(file.Status, status, StringComparison.OrdinalIgnoreCase)) ?? 0;

    private bool StatusFilterIsActive(string status) =>
        string.Equals(_statusFilter, status, StringComparison.OrdinalIgnoreCase);

    private string StatusFilterClass(string? status) =>
        status is null ? _statusFilter is null ? "git-file-status-filter is-active" : "git-file-status-filter"
            : StatusFilterIsActive(status) ? "git-file-status-filter is-active" : "git-file-status-filter";

    private static int StatusOrder(string status) => status switch
    {
        "modified" => 0,
        "added" => 1,
        "deleted" => 2,
        "renamed" => 3,
        "copied" => 4,
        _ => 5
    };

    private static string StatusIcon(string status) => status.ToLowerInvariant() switch
    {
        "added" => "note_add",
        "deleted" => "delete",
        "renamed" => "drive_file_rename",
        "copied" => "file_copy",
        _ => "description"
    };

    internal static string StatusIconClass(string status) => status.ToLowerInvariant() switch
    {
        "added" => "git-file-status-icon diff-file-stat-add",
        "deleted" => "git-file-status-icon diff-file-stat-del",
        "renamed" or "copied" => "git-file-status-icon omni-u-text-accent",
        _ => "git-file-status-icon omni-u-text-muted"
    };

    internal static IReadOnlyList<DiffLineView> BuildDiffLines(string patch)
    {
        var result = new List<DiffLineView>();
        var oldLine = 0;
        var newLine = 0;
        var inHunk = false;

        foreach (var line in patch.Replace("\r", string.Empty).Split('\n'))
        {
            if (TryParseHunkStarts(line, out var oldStart, out var newStart))
            {
                oldLine = oldStart;
                newLine = newStart;
                inHunk = true;
                result.Add(new DiffLineView(line, null, null));
            }
            else if (inHunk && line.StartsWith('+') && !line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                result.Add(new DiffLineView(line, null, newLine++));
            }
            else if (inHunk && line.StartsWith('-') && !line.StartsWith("--- ", StringComparison.Ordinal))
            {
                result.Add(new DiffLineView(line, oldLine++, null));
            }
            else if (inHunk && line.StartsWith(' '))
            {
                result.Add(new DiffLineView(line, oldLine++, newLine++));
            }
            else
            {
                result.Add(new DiffLineView(line, null, null));
            }
        }

        return result;
    }

    private static bool TryParseHunkStarts(string line, out int oldStart, out int newStart)
    {
        oldStart = 0;
        newStart = 0;
        if (!line.StartsWith("@@ -", StringComparison.Ordinal)) return false;

        var oldTokenEnd = line.IndexOf(' ', 4);
        if (oldTokenEnd < 0 || oldTokenEnd + 2 >= line.Length || line[oldTokenEnd + 1] != '+') return false;

        var newTokenEnd = line.IndexOf(' ', oldTokenEnd + 1);
        if (newTokenEnd < 0) return false;

        return TryParseLineStart(line.AsSpan(4, oldTokenEnd - 4), out oldStart)
            && TryParseLineStart(line.AsSpan(oldTokenEnd + 2, newTokenEnd - oldTokenEnd - 2), out newStart);
    }

    private static bool TryParseLineStart(ReadOnlySpan<char> token, out int start)
    {
        var commaIndex = token.IndexOf(',');
        var startToken = commaIndex >= 0 ? token[..commaIndex] : token;
        return int.TryParse(startToken, out start);
    }

    internal static string LineClass(string line)
    {
        if (line.StartsWith("@@", StringComparison.Ordinal)) return "diff-line diff-line-hunk";
        if (line.Length > 0 && line[0] == '+' && !line.StartsWith("+++ ", StringComparison.Ordinal)) return "diff-line diff-line-add";
        if (line.Length > 0 && line[0] == '-' && !line.StartsWith("--- ", StringComparison.Ordinal)) return "diff-line diff-line-del";
        return "diff-line";
    }

    private static OmniTone StatusBadge(string status) => status switch
    {
        "added" => OmniTone.Success,
        "deleted" => OmniTone.Danger,
        "renamed" => OmniTone.Accent,
        _ => OmniTone.Neutral
    };

    private string LocalizeStatus(string status) => status.ToLowerInvariant() switch
    {
        "added" => L["Added"],
        "modified" => L["Modified"],
        "deleted" => L["Deleted"],
        "renamed" => L["Renamed"],
        "copied" => L["Copied"],
        _ => string.Format(L["GitStatusUnknown"], status)
    };

    internal sealed record DiffLineView(string Text, int? OldLineNumber, int? NewLineNumber);
}
