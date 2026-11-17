// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Pipelines;

/// <summary>Where the optional first push of the setup wizard stands (R-20).</summary>
public enum PipelineSetupFirstPushPhase
{
    /// <summary>Nothing offered, or the offer was skipped: the wizard goes on as it always did.</summary>
    None,

    /// <summary>The repository was just created: the push commands are shown and the user chooses to wait for his push or not.</summary>
    Offered,

    /// <summary>The user accepted: the wizard waits for the code to reach the repository and does not create the pipelines
    /// before (R-6: the pipelines are created once the code is there).</summary>
    Waiting,

    /// <summary>The repository holds a branch: the wizard read the repository, the branches and the production setup again.</summary>
    Received
}

/// <summary>
/// R-20 and R-6 (user decision 2026-10-29 and 2026-10-30): once the wizard created the project's Git repository, it offers an
/// optional first push, shows the commands to copy (<c>main</c> then <c>develop</c>), and, if the user accepts, waits for what
/// is pushed and goes on as soon as the repository holds a branch. Skipping it leaves the wizard as it was: nothing is waited for.
/// The reception is read from the repository's branches, the way the rest of the wizard reads them, from the page at
/// <see cref="PollInterval"/>: no loop runs on the server. The git hub also pushes a BranchesChanged event, but listening to it
/// needs a connection to start, join and rejoin; one small read at a moderate pace needs none of that.
/// </summary>
public sealed class PipelineSetupFirstPush(ApiClient api, Action changed, Func<int, Task> received)
{
    /// <summary>A moderate pace: one small read of the branches every few seconds, only while the offer is on screen or the user is waiting.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    public PipelineSetupFirstPushPhase Phase { get; private set; }

    /// <summary>True while the wizard waits for the push: the creation of the pipelines is held back.</summary>
    public bool Waiting => Phase == PipelineSetupFirstPushPhase.Waiting;

    /// <summary>True while the repository is still expected to receive its code: waiting, or offered and not yet answered. A
    /// push made from a terminal without choosing to wait must not leave a panel saying the repository is empty.</summary>
    public bool Watching => Phase is PipelineSetupFirstPushPhase.Offered or PipelineSetupFirstPushPhase.Waiting;

    public int? RepositoryId { get; private set; }

    /// <summary>R-52: the address of the repository alone, for the field next to the commands (empty before the repository was read).</summary>
    public string CloneUrl => _cloneUrl ?? string.Empty;

    private string? _cloneUrl;
    private string _defaultBranch = "main";

    /// <summary>The commands that put a local project on <c>main</c> then <c>develop</c> of the new repository.</summary>
    public string Commands
    {
        get
        {
            var lines = new List<string>
            {
                $"git remote add origin {_cloneUrl}",
                $"git branch -M {_defaultBranch}",
                $"git push -u origin {_defaultBranch}"
            };
            if (!string.Equals(_defaultBranch, PipelineSetupSourceBranch.Preferred, StringComparison.Ordinal))
                lines.Add($"git push origin {_defaultBranch}:{PipelineSetupSourceBranch.Preferred}");
            return string.Join('\n', lines);
        }
    }

    /// <summary>Reads the repository just created, for its address and default branch, and offers the first push. False when the
    /// repository cannot be read (no address, so no command): the caller then goes on as before.</summary>
    public async Task<bool> OfferAsync(int repositoryId)
    {
        GitLightRepoDto? repository;
        try
        {
            repository = await api.Git.GetGitRepoAsync(repositoryId);
        }
        catch (Exception exception) when (RunParametersReading.IsTransportFailure(exception))
        {
            return false;
        }

        if (string.IsNullOrEmpty(repository?.CloneUrl)) return false;
        _cloneUrl = repository.CloneUrl;
        _defaultBranch = string.IsNullOrWhiteSpace(repository.DefaultBranch) ? "main" : repository.DefaultBranch;
        RepositoryId = repositoryId;
        Phase = PipelineSetupFirstPushPhase.Offered;
        changed();
        return true;
    }

    public void Accept()
    {
        if (Phase != PipelineSetupFirstPushPhase.Offered) return;
        Phase = PipelineSetupFirstPushPhase.Waiting;
        changed();
    }

    /// <summary>Stops offering or waiting: the wizard is as it was without the step.</summary>
    public void Skip()
    {
        Phase = PipelineSetupFirstPushPhase.None;
        changed();
    }

    /// <summary>Another project: the repository created for the previous one is no longer this one's.</summary>
    public void Forget()
    {
        RepositoryId = null;
        Skip();
    }

    /// <summary>One look at the repository. When it holds a branch the push is received: the wizard is told (it reads the
    /// readiness, the branches and the production setup again) and the wait ends. A read that fails is a pass lost, not
    /// a verdict: the wait goes on.</summary>
    public async Task<bool> CheckAsync()
    {
        if (!Watching || RepositoryId is not { } repositoryId) return false;
        try
        {
            var branches = await api.Git.GetGitBranchesAsync(repositoryId);
            if (branches.Count == 0) return false;
        }
        catch (Exception exception) when (RunParametersReading.IsTransportFailure(exception))
        {
            return false;
        }

        Phase = PipelineSetupFirstPushPhase.Received;
        await received(repositoryId);
        changed();
        return true;
    }
}
