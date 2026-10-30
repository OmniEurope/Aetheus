// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Projects.ProjectDetailSections;

public partial class ProjectExternalRepoSection : ProjectWritableSectionBase
{
    [Inject] private UiActions Ui { get; set; } = default!;

    private bool _busy;
    private bool _syncing;
    private ExternalRepoDto? _repo;
    private AttachModel _model = new();

    private List<OmniOption<GitProviderType>> ProviderOptions =>
        [.. Enum.GetValues<GitProviderType>().Select(t => new OmniOption<GitProviderType>(t, L.Localize(t)))];
    private List<OmniOption<GitAuthType>> AuthTypeOptions =>
        [.. Enum.GetValues<GitAuthType>().Select(t => new OmniOption<GitAuthType>(t, L.Localize(t)))];

    protected override async Task LoadAsync()
    {
        _loading = true;
        try
        {
            _repo = await Api.Git.GetExternalRepoAsync(ProjectId);
        }
        catch (HttpRequestException) { _repo = null; }
        finally { _loading = false; }
    }

    private async Task AttachAsync()
    {
        _busy = true;
        try
        {
            await Ui.RunAsync(
                () => Api.Git.AttachExternalRepoAsync(new AttachExternalRepoRequest
                {
                    ProjectId = ProjectId,
                    ProviderType = _model.ProviderType,
                    BaseUrl = _model.BaseUrl,
                    OwnerOrGroup = _model.OwnerOrGroup,
                    RepositoryName = _model.RepositoryName,
                    DefaultBranch = _model.DefaultBranch,
                    AuthType = _model.AuthType,
                    Username = _model.Username,
                    Token = _model.Token,
                    PrivateKeyPem = _model.PrivateKeyPem,
                    Passphrase = _model.Passphrase,
                    KnownHosts = _model.KnownHosts,
                    AutoSyncEnabled = _model.AutoSyncEnabled,
                    FetchIntervalMinutes = _model.FetchIntervalMinutes,
                    WriteEnabled = _model.WriteEnabled
                }),
                "ExternalRepoAttached",
                async _ => await LoadAsync(),
                successTitleKey: "Saved");
        }
        finally { _busy = false; }
    }

    private async Task SyncNowAsync()
    {
        _syncing = true;
        StateHasChanged();
        try
        {
            var result = await Api.Git.SyncExternalRepoNowAsync(ProjectId);
            if (result is not null) { _repo = result; Toast.Success("Saved", "ExternalRepoSyncQueued"); }
            else Toast.Error("Error", "SaveFailed");
        }
        catch (HttpRequestException) { Toast.Error("Error", "SaveFailed"); }
        finally { _syncing = false; StateHasChanged(); }
    }

    private async Task DetachAsync()
    {
        var confirmed = await Dialog.Confirm(L["DetachExternalRepoConfirm"].Value, L["Detach"].Value,
            new OmniConfirmOptions { Destructive = true, OkButtonText = L["Detach"].Value, CancelButtonText = L["GoBack"].Value });
        if (confirmed != true) return;

        await Veil.RunAsync(async () =>
        {
            var status = await Api.Git.DetachExternalRepoAsync(ProjectId);
            if (status.Success) { _repo = null; StateHasChanged(); }
            else Toast.Error("Error", "DeleteFailed");
        });
    }

    /// <summary>What a pipeline of the project writes to take its workspace from this repository.</summary>
    private string PipelineSourceYaml =>
        $"source:\n  repository: {_repo?.Slug}\n  branch: {_repo?.DefaultBranch ?? "main"}";

    private static OmniTone MirrorBadge(GitMirrorStatus status) => status switch
    {
        GitMirrorStatus.Ready => OmniTone.Success,
        GitMirrorStatus.Syncing => OmniTone.Accent,
        GitMirrorStatus.Error => OmniTone.Danger,
        _ => OmniTone.Neutral
    };

    private sealed class AttachModel
    {
        public GitProviderType ProviderType { get; set; } = GitProviderType.GitHub;

        [StringLength(500)]
        public string? BaseUrl { get; set; }

        [Required]
        [StringLength(200)]
        public string OwnerOrGroup { get; set; } = string.Empty;

        [Required]
        [StringLength(200)]
        public string RepositoryName { get; set; } = string.Empty;

        [StringLength(200)]
        public string? DefaultBranch { get; set; }

        public GitAuthType AuthType { get; set; } = GitAuthType.HttpsToken;

        [StringLength(200)]
        public string? Username { get; set; }

        [StringLength(4000)]
        public string? Token { get; set; }

        [StringLength(20000)]
        public string? PrivateKeyPem { get; set; }

        [StringLength(500)]
        public string? Passphrase { get; set; }

        [StringLength(8000)]
        public string? KnownHosts { get; set; }

        public bool AutoSyncEnabled { get; set; } = true;

        [Range(1, 1440)]
        public int FetchIntervalMinutes { get; set; } = 15;

        public bool WriteEnabled { get; set; }
    }
}
