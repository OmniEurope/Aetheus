// SPDX-License-Identifier: EUPL-1.2
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// The branch the pipelines the wizard creates read (recette R-94, user decision 2026-10-25). The wizard proposes
/// <c>develop</c> when the project's repository has it, else the project's default branch, lets the user pick another
/// branch of the repository, and writes the result into every pipeline as <c>source_branch:</c> and as the pipeline's
/// source branch; once they exist the same branch is written to the project's default branch (R-31, <see cref="WriteProjectDefaultAsync"/>).
/// Without it a pipeline reads the project's default branch (main for a project whose default is main),
/// which is not where the work is delivered.
/// The production analysis reads the branch chosen here (R-9), see <see cref="PipelineSetupProduction"/>.
/// </summary>
internal sealed class PipelineSetupSourceBranch(ApiClient api, Action changed)
{
    /// <summary>The branch proposed when the repository has it.</summary>
    public const string Preferred = "develop";

    private ProjectDto? _project;
    private int? _readProjectId;
    private int? _repositoryId;
    private IReadOnlyList<string> _branches = [];
    private string? _repositoryDefaultBranch;
    private Task _reading = Task.CompletedTask;
    private bool _read;
    private bool _readFailed;

    /// <summary>The branch the user picked or the address named; null while the proposal stands. Only this one travels in
    /// the summary's address: the proposal is recomputed from the repository.</summary>
    public string? Picked { get; private set; }

    /// <summary>The branch the pipelines are created on; null when neither the repository nor the project name one.</summary>
    public string? Chosen => Picked ?? Propose(_branches, _project?.DefaultBranch, _repositoryDefaultBranch);

    /// <summary>True once everything that could name a branch was read and nothing did: no pick, no <c>develop</c>, no default
    /// branch on the project, none on its repository (or no internal repository at all). The pipelines would then be
    /// written without <c>source_branch:</c> (R-76); the wizard says so instead of staying silent.</summary>
    public bool IsMissing => _project is not null && _read && !_readFailed && Chosen is null;

    /// <summary>The repository's branches, and the chosen one when it is not among them (the default branch of a repository
    /// that could not be listed).</summary>
    public IReadOnlyList<OmniOption<string>> Options =>
    [
        .. _branches.Concat(Chosen is { } chosen && !_branches.Contains(chosen) ? [chosen] : [])
            .Select(name => new OmniOption<string>(name, name))
    ];

    /// <summary>Completes once the repository's branches that are being read are: the creation waits on it, so asked while
    /// they were still loading it does not fall back on the default branch.</summary>
    public Task SettledAsync() => _reading;

    /// <summary>The proposal: <c>develop</c> when the repository has it, else the project's default branch, else the
    /// repository's own (a project created without a default branch, R-76: W7 got pipelines with no <c>source_branch:</c>).</summary>
    internal static string? Propose(IReadOnlyCollection<string> branches, string? defaultBranch,
        string? repositoryDefaultBranch = null) =>
        branches.Contains(Preferred, StringComparer.Ordinal) ? Preferred
        : !string.IsNullOrWhiteSpace(defaultBranch) ? defaultBranch
        : string.IsNullOrWhiteSpace(repositoryDefaultBranch) ? null : repositoryDefaultBranch;

    /// <summary>R-31 (user decision 2026-11-08): the branch the pipelines were written on is also written to the project's default
    /// branch, so the overview and the pipelines say the same. Nothing is written when no pipeline was created or no branch is
    /// known. Throws <see cref="InvalidOperationException"/> with <paramref name="failed"/> when the server refuses, so the
    /// wizard rolls the pipelines back.</summary>
    public async Task WriteProjectDefaultAsync(int projectId, bool pipelinesCreated, string failed)
    {
        if (!pipelinesCreated || Chosen is not { } branch) return;
        if (!(await api.Projects.SetDefaultBranchAsync(projectId, branch)).Success)
            throw new InvalidOperationException(failed);
    }

    public void Pick(string? branch)
    {
        Picked = string.IsNullOrWhiteSpace(branch) ? null : branch.Trim();
        changed();
    }

    /// <summary>Follows the project the wizard is on: another project forgets the previous project's pick and reads its own
    /// branches. A project already read is not read again.</summary>
    public void Follow(ProjectDto? project)
    {
        if (project?.Id == _readProjectId) return;
        _project = project;
        _readProjectId = project?.Id;
        _repositoryId = project?.InternalRepositoryId;
        Picked = null;
        _branches = [];
        _repositoryDefaultBranch = null;
        _readFailed = false;
        _read = project is not null && project.InternalRepositoryId is null;
        _reading = project?.InternalRepositoryId is { } repositoryId ? ReadAsync(project.Id, repositoryId) : Task.CompletedTask;
    }

    /// <summary>R-20: the repository was created, or received its first push, after the wizard loaded the project (which then
    /// names no repository): its branches are read again, the pick kept, so <c>develop</c> is proposed once it is pushed.
    /// Completes when the reading did, like <see cref="SettledAsync"/>.</summary>
    public Task RereadAsync(int repositoryId)
    {
        if (_project is null) return Task.CompletedTask;
        _repositoryId = repositoryId;
        _read = false;
        _readFailed = false;
        return _reading = ReadAsync(_project.Id, repositoryId);
    }

    /// <summary>R-6: asked just before the pipelines are written. The branches were read when the project was chosen; code pushed
    /// since (from a terminal, without the first-push offer) adds <c>develop</c>, which the pipelines must then read. Only a
    /// proposal can change, so a branch the user picked is left alone, and a project with no repository has nothing to read.
    /// <paramref name="createdRepositoryId"/> is the repository the wizard itself created for the project: the project was loaded
    /// before it existed and names none, and without it a push made from a terminal after the creation would never be seen.</summary>
    public Task RefreshAsync(int? createdRepositoryId = null) =>
        Picked is null && (_repositoryId ?? createdRepositoryId) is { } repositoryId ? RereadAsync(repositoryId) : Task.CompletedTask;

    private async Task ReadAsync(int projectId, int repositoryId)
    {
        await Task.WhenAll(ReadBranchesAsync(projectId, repositoryId), ReadRepositoryDefaultAsync(projectId, repositoryId));
        // The answer of a project the wizard has left says nothing about the one it is on.
        if (_readProjectId == projectId) _read = true;
        changed();
    }

    private async Task ReadBranchesAsync(int projectId, int repositoryId)
    {
        try
        {
            var branches = await api.Git.GetGitBranchesAsync(repositoryId);
            // The answer of a project the wizard has left says nothing about the one it is on.
            if (_readProjectId == projectId)
                _branches = [.. branches.Select(branch => branch.Name).Where(name => !string.IsNullOrWhiteSpace(name))];
        }
        catch (Exception exception) when (RunParametersReading.IsTransportFailure(exception))
        {
            // Nothing is read, so nothing is claimed: the project's default branch stands.
            _readFailed = true;
        }
    }

    /// <summary>The repository's own default branch: what the pipelines read when the project names none and the repository
    /// has no develop (a repository still empty lists no branch, so the branches cannot give it).</summary>
    private async Task ReadRepositoryDefaultAsync(int projectId, int repositoryId)
    {
        try
        {
            var repository = await api.Git.GetGitRepoAsync(repositoryId);
            if (_readProjectId == projectId) _repositoryDefaultBranch = repository?.DefaultBranch;
        }
        catch (Exception exception) when (RunParametersReading.IsTransportFailure(exception))
        {
            // Nothing is read, so nothing is claimed.
            _readFailed = true;
        }
    }
}
