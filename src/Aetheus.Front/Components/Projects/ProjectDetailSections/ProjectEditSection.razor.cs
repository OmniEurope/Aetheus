// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;
using Aetheus.Front.Components.Projects;
namespace Aetheus.Front.Components.Projects.ProjectDetailSections;

public partial class ProjectEditSection
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private ILogger<ProjectEditSection> Logger { get; set; } = default!;

    [Parameter, EditorRequired] public ProjectDetailDto? Project { get; set; }
    [Parameter] public EventCallback OnSaved { get; set; }

    /// <summary>Raised once the migration gate setting was saved, so the page reloads the project the overview reads (the
    /// expand/contract tab follows the setting) without leaving the settings.</summary>
    [Parameter] public EventCallback OnMigrationGateChanged { get; set; }

    private ProjectFormModel _model = new();
    private int? _modelProjectId;
    private bool _saving;
    private List<OmniOption<ProjectStatus>> _statusOptions = [];

    // DefaultBranch dropdown: the project's internal repo branches (empty => fall back to a free-text box).
    private List<string> _branches = [];
    private bool _branchesLoaded;
    private int? _branchesProjectId;
    private string? _repositoryDefaultBranch;

    // PLAN-007: the automatic dependency updates setting, null until read (or when it could not be read).
    private bool? _dependencyUpdatesEnabled;
    private bool _savingDependencyUpdates;

    // R-13: the migration gate (expand/contract model) setting, read from the project.
    private bool _migrationGateEnabled;
    private bool _savingMigrationGate;

    private bool DefaultBranchesDiffer =>
        !string.IsNullOrWhiteSpace(_model.DefaultBranch)
        && !string.IsNullOrWhiteSpace(_repositoryDefaultBranch)
        && !string.Equals(_model.DefaultBranch, _repositoryDefaultBranch, StringComparison.OrdinalIgnoreCase);

    protected override void OnParametersSet()
    {
        _statusOptions = ProjectFormModel.BuildStatusOptions(L);
        if (Project is not null && !_savingMigrationGate)
            _migrationGateEnabled = Project.MigrationGateEnabled;

        // Preserve in-progress edits when the parent rerenders the same project. Rebuilding the
        // model on every parameter pass could visibly restore the stored branch after the user
        // selected another one in the dropdown.
        if (Project is not null && _modelProjectId != Project.Id)
        {
            _model = ProjectFormModel.From(Project);
            _modelProjectId = Project.Id;
        }
    }

    protected override async Task OnParametersSetAsync()
    {
        if (Project is null || _branchesProjectId == Project.Id) return;
        _branchesProjectId = Project.Id;
        await Task.WhenAll(LoadBranchesAsync(), LoadDependencyUpdatesAsync());
    }

    private async Task LoadDependencyUpdatesAsync()
    {
        _dependencyUpdatesEnabled = null;
        try
        {
            _dependencyUpdatesEnabled = (await Api.Analysis.GetDependencyUpdatesAsync(Project!.Id)).Enabled;
        }
        catch (HttpRequestException ex)
        {
            Logger.LogDebug(ex, "Could not read the dependency updates setting of project {ProjectId}", Project!.Id);
        }
    }

    private async Task SetDependencyUpdatesAsync(bool enabled)
    {
        _savingDependencyUpdates = true;
        try
        {
            await Api.Analysis.SetDependencyUpdatesEnabledAsync(Project!.Id, enabled);
            _dependencyUpdatesEnabled = enabled;
            Toast.Success("Saved", "ProjectUpdated");
        }
        catch (HttpRequestException)
        {
            Toast.Error("Error", "SaveFailed");
        }
        finally
        {
            _savingDependencyUpdates = false;
        }
    }

    private async Task SetMigrationGateAsync(bool enabled)
    {
        _savingMigrationGate = true;
        try
        {
            if (!(await Api.Projects.SetMigrationGateEnabledAsync(Project!.Id, enabled)).Success)
            {
                Toast.Error("Error", "SaveFailed");
                return;
            }
            _migrationGateEnabled = enabled;
            Toast.Success("Saved", "ProjectUpdated");
            await OnMigrationGateChanged.InvokeAsync();
        }
        catch (HttpRequestException)
        {
            Toast.Error("Error", "SaveFailed");
        }
        finally
        {
            _savingMigrationGate = false;
        }
    }

    private async Task LoadBranchesAsync()
    {
        _branches = [];
        _repositoryDefaultBranch = null;
        // Best-effort: the branch list only enriches the DefaultBranch picker. A repo without an
        // internal git repo (external-only / not yet cloned) or a transient failure just falls back
        // to the free-text box - never blocks editing.
        try
        {
            var repos = await Api.Git.GetGitReposAsync(Project!.Id);
            var repo = repos.Count == 1 ? repos[0] : null;
            if (repo is not null)
            {
                _repositoryDefaultBranch = repo.DefaultBranch;
                _branches = (await Api.Git.GetGitBranchesAsync(repo.Id)).Select(b => b.Name).ToList();
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException)
        {
            Logger.LogDebug(ex,
                "Could not load internal repository branches for project {ProjectId}; keeping the stored branch as free text",
                Project!.Id);
            _branches = [];
            _repositoryDefaultBranch = null;
        }

        _branchesLoaded = _branches.Count > 0;
        _model.DefaultBranch = ResolveDefaultBranch(_branches, _model.DefaultBranch);
        if (_branchesLoaded && !_branches.Any(branch =>
                branch.Equals(_model.DefaultBranch, StringComparison.OrdinalIgnoreCase)))
            _branches.Insert(0, _model.DefaultBranch);
        StateHasChanged();
    }

    /// <summary>Case-insensitive default-branch resolution: keep an existing value (normalised to the
    /// repo's actual branch casing) when it still exists. A non-empty stored value that is absent
    /// from the repository is preserved so merely opening and saving the form cannot silently
    /// rewrite configuration. Only an empty value falls back to <c>main</c>, <c>master</c>, then the
    /// first branch.</summary>
    internal static string ResolveDefaultBranch(IReadOnlyList<string> branches, string? current)
    {
        if (branches.Count == 0)
            return string.IsNullOrWhiteSpace(current) ? "main" : current;

        if (!string.IsNullOrWhiteSpace(current))
        {
            var match = branches.FirstOrDefault(b => b.Equals(current, StringComparison.OrdinalIgnoreCase));
            return match ?? current;
        }

        return branches.FirstOrDefault(b => b.Equals("main", StringComparison.OrdinalIgnoreCase))
            ?? branches.FirstOrDefault(b => b.Equals("master", StringComparison.OrdinalIgnoreCase))
            ?? branches[0];
    }

    private async Task OnSubmit()
    {
        _saving = true;
        var updated = await Api.Projects.UpdateProjectAsync(Project!.Id, _model.ToUpdateRequest());

        if (updated is not null)
        {
            Toast.Success("Saved", "ProjectUpdated");
            await OnSaved.InvokeAsync();
        }
        else
        {
            Toast.Error("Error", "SaveFailed");
        }
        _saving = false;
    }


}
