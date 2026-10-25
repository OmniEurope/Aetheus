// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Git;

internal sealed class GitRepositoryDetailActions(
    ApiClient api,
    OmniDialogService dialog,
    NotifyHelper toast,
    IStringLocalizer<AppStrings> localizer)
{
    public async Task<bool> CreateBranchAsync(int repositoryId)
        => await OpenCreateDialogAsync<GitBranchCreateDialog>(
            repositoryId, "NewBranch", "400px").ConfigureAwait(false);

    public async Task<bool> DeleteBranchAsync(int repositoryId, GitLightBranchDto branch)
    {
        var confirmed = await ConfirmDeleteAsync(branch.Name);
        if (!confirmed) return false;
        var success = await api.Git.DeleteGitBranchAsync(repositoryId, branch.Name);
        if (success) toast.Success("Deleted", branch.Name);
        return success;
    }

    public async Task<bool> CreateTagAsync(int repositoryId)
        => await OpenCreateDialogAsync<GitTagCreateDialog>(
            repositoryId, "NewTag", "400px").ConfigureAwait(false);

    private async Task<bool> OpenCreateDialogAsync<TDialog>(
        int repositoryId,
        string titleKey,
        string width)
        where TDialog : ComponentBase
    {
        var result = await dialog.OpenAsync<TDialog>(
            localizer[titleKey].Value,
            new Dictionary<string, object?>
            {
                ["RepoId"] = repositoryId,
                ["Branches"] = await api.Git.GetGitBranchesAsync(repositoryId)
            },
            new OmniDialogOptions { Width = width, AutoFocusFirstElement = false });
        return result is true;
    }

    public async Task<bool> DeleteTagAsync(int repositoryId, GitLightTagDto tag)
    {
        var confirmed = await ConfirmDeleteAsync(tag.Name);
        if (!confirmed) return false;
        var success = await api.Git.DeleteGitTagAsync(repositoryId, tag.Name);
        if (success) toast.Success("Deleted", tag.Name);
        return success;
    }

    public async Task<bool> CreatePullRequestAsync(int repositoryId)
    {
        var result = await dialog.OpenAsync<GitPrCreateDialog>(
            localizer["NewPullRequest"].Value,
            new Dictionary<string, object?>
            {
                ["RepoId"] = repositoryId,
                ["Branches"] = await api.Git.GetGitBranchesAsync(repositoryId)
            },
            new OmniDialogOptions { Width = "500px", AutoFocusFirstElement = false });
        return result is true;
    }

    public async Task<bool> MergePullRequestAsync(
        int repositoryId, InternalPullRequestDto pullRequest)
    {
        var confirmed = await dialog.Confirm(
            string.Format(localizer["MergeConfirm"].Value, pullRequest.Number),
            localizer["Merge"].Value,
            new OmniConfirmOptions
            {
                OkButtonText = localizer["Merge"].Value,
                CancelButtonText = localizer["GoBack"].Value
            });
        if (confirmed != true) return false;
        var result = await api.Git.MergeGitPullRequestAsync(repositoryId, pullRequest.Number);
        if (result is not null) toast.Success("Merged", $"#{pullRequest.Number}");
        return result is not null;
    }

    public async Task<bool> ClosePullRequestAsync(
        int repositoryId, InternalPullRequestDto pullRequest)
    {
        var confirmed = await dialog.Confirm(
            string.Format(localizer["CloseConfirm"].Value, pullRequest.Number),
            localizer["Close"].Value,
            new OmniConfirmOptions
            {
                Destructive = true,
                ConfirmIcon = OmniIconName.Prohibit,
                OkButtonText = localizer["Close"].Value,
                CancelButtonText = localizer["GoBack"].Value
            });
        if (confirmed != true) return false;
        var result = await api.Git.CloseGitPullRequestAsync(repositoryId, pullRequest.Number);
        if (result is not null) toast.Success("Closed", $"#{pullRequest.Number}");
        return result is not null;
    }

    public async Task<bool> AddProtectionRuleAsync(int repositoryId)
    {
        var result = await dialog.OpenAsync<GitBranchProtectionDialog>(
            localizer["AddRule"].Value,
            new Dictionary<string, object?> { ["RepoId"] = repositoryId },
            new OmniDialogOptions { Width = "450px", AutoFocusFirstElement = false });
        return result is true;
    }

    /// <summary>Designates the repository as the project's configuration repository (the previous one stops being it)
    /// or gives the role up (ADR-057). The server refuses a mirror and the project's last source repository.</summary>
    public async Task<bool> SetConfigurationAsync(int repositoryId, bool designate)
    {
        var updated = await api.Git.UpdateGitRepoAsync(
            repositoryId, new UpdateGitLightRepoRequest { IsConfiguration = designate });
        if (updated is null)
        {
            toast.Error("Error", "SaveFailed");
            return false;
        }

        toast.Success(
            localizer["Saved"].Value,
            localizer[designate ? "GitRepoConfigurationDesignated" : "GitRepoConfigurationReleased"].Value);
        return true;
    }

    public async Task<bool> DeleteProtectionRuleAsync(
        int repositoryId, BranchProtectionRuleDto rule)
    {
        var confirmed = await ConfirmDeleteAsync(rule.Pattern);
        if (!confirmed) return false;
        var success = await api.Git.DeleteGitBranchProtectionRuleAsync(repositoryId, rule.Id);
        if (success) toast.Success("Deleted", rule.Pattern);
        return success;
    }

    public async Task<bool> UpdateProtectionRuleAsync(
        int repositoryId,
        BranchProtectionRuleDto rule,
        bool? preventDeletion,
        bool? preventForcePush,
        BranchProtectionLevel? protectionLevel)
    {
        var result = await api.Git.UpdateGitBranchProtectionRuleAsync(
            repositoryId,
            rule.Id,
            new UpdateBranchProtectionRuleRequest
            {
                PreventDeletion = preventDeletion ?? rule.PreventDeletion,
                PreventForcePush = preventForcePush ?? rule.PreventForcePush,
                ProtectionLevel = protectionLevel ?? rule.ProtectionLevel
            });
        return result is not null;
    }

    private async Task<bool> ConfirmDeleteAsync(string name) =>
        await dialog.Confirm(
            string.Format(localizer["DeleteConfirm"].Value, name),
            localizer["Delete"].Value,
            new OmniConfirmOptions
            {
                Destructive = true,
                OkButtonText = localizer["Delete"].Value,
                CancelButtonText = localizer["GoBack"].Value
            }) == true;
}
