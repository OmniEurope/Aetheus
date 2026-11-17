// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Shared.Components.ExternalRepos;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// R-23 (user decision 2026-11-09): a project whose source is an external repository is read through its internal mirror,
/// which holds nothing until it is synchronized. The wizard synchronizes that mirror (the call the repository page makes) before
/// it reads the repository for the first time, once per project, instead of reading an empty mirror and saying production was
/// not prepared. <see cref="Busy"/> is the visible progress state; a refused or failed synchronization is kept in
/// <see cref="Failure"/> and never hidden, and a later call tries again.
/// R-27: a project that only names an external address (no mirror, no repository of its own) has nothing to synchronize yet:
/// the mirror is created first, through the same endpoint as the project page (Write permission, public address, connectivity
/// probe and endpoint guard all stay on the server; the address is never read nor sent from here, no credential is stored), then
/// synchronized. A refusal is kept in <see cref="Failure"/> with the server's own words.
/// </summary>
internal sealed class PipelineSetupMirrorSync(ApiClient api, IStringLocalizer<AppStrings> localizer, Action changed)
{
    private int? _projectId;
    private Task _running = Task.CompletedTask;

    /// <summary>True while the mirror is being created or synchronized.</summary>
    public bool Busy { get; private set; }

    /// <summary>Why the last creation or synchronization failed, in words the user can read; null when none failed.</summary>
    public string? Failure { get; private set; }

    /// <summary>Synchronizes the mirror of <paramref name="projectId"/> unless it already was, or the project has no external
    /// repository; creates it first when <paramref name="repositoryAddress"/>, the address the project names, is an external one
    /// nothing mirrors. Completes when the work did; a second call while it runs waits for the same one.</summary>
    public Task EnsureAsync(int projectId, string? repositoryAddress = null)
    {
        if (_projectId == projectId) return _running;
        _projectId = projectId;
        return _running = RunAsync(projectId, repositoryAddress);
    }

    /// <summary>Forgets what was done, so the next <see cref="EnsureAsync"/> synchronizes again (the retry of the user).</summary>
    public void Forget()
    {
        _projectId = null;
        Failure = null;
    }

    /// <summary>R-27: the external address the project names that nothing mirrors, null when it names none, when it has a
    /// repository of its own (the address is then not its source) or when it cannot be read. Read only to know whether the
    /// mirror is to be created; the address is never sent anywhere.</summary>
    public async Task<string?> ReadUnmirroredAddressAsync(int projectId)
    {
        try
        {
            var address = (await api.Projects.GetProjectDetailAsync(projectId))?.RepositoryUrl;
            return ExternalRepositoryAddress.IsUnmirrored(address) && !await HasOwnRepositoryAsync(projectId) ? address : null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private async Task<bool> HasOwnRepositoryAsync(int projectId) =>
        (await api.Git.GetGitReposAsync(projectId)).Exists(repository => !repository.IsConfiguration);

    private async Task RunAsync(int projectId, string? repositoryAddress)
    {
        Busy = true;
        Failure = null;
        changed();
        try
        {
            var attached = await api.Git.GetExternalRepoAsync(projectId);
            var created = attached is null && ExternalRepositoryAddress.IsUnmirrored(repositoryAddress);
            if (created) attached = await CreateMirrorAsync(projectId);
            if (attached is null) return;
            // A mirror just created was cloned by the server already; any other is synchronized now.
            Failure = FailureOf(created ? attached : await api.Git.SyncExternalRepoNowAsync(projectId));
        }
        catch (HttpRequestException exception)
        {
            Failure = string.Format(localizer["PipelineSetupMirrorSyncFailed"], exception.Message);
        }
        finally
        {
            Busy = false;
            // A failure is retried by the next call, a success is not repeated.
            if (Failure is not null && _projectId == projectId) _projectId = null;
            changed();
        }
    }

    /// <summary>Creates the project mirror of the address; null when the project already has a repository of its own (the
    /// address is then not its source and nothing is created) or when the creation failed, said in <see cref="Failure"/>.</summary>
    private async Task<ExternalRepoDto?> CreateMirrorAsync(int projectId)
    {
        if (await HasOwnRepositoryAsync(projectId)) return null;
        var outcome = await api.Git.MirrorProjectRepositoryAddressAsync(projectId);
        if (outcome.Value is not null) return outcome.Value;
        Failure = string.Format(localizer["PipelineSetupMirrorCreateFailed"],
            string.IsNullOrWhiteSpace(outcome.Error?.Message) ? localizer["OperationFailed"].Value : outcome.Error.Message);
        return null;
    }

    private string? FailureOf(ExternalRepoDto? synchronized) => synchronized switch
    {
        null => string.Format(localizer["PipelineSetupMirrorSyncFailed"], localizer["OperationFailed"].Value),
        { MirrorStatus: GitMirrorStatus.Error } => string.Format(localizer["PipelineSetupMirrorSyncFailed"],
            string.IsNullOrWhiteSpace(synchronized.LastFetchError) ? localizer["OperationFailed"].Value : synchronized.LastFetchError),
        _ => null
    };
}
