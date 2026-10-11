// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Git;

public partial class GitBranchProtectionDialog
{
    [Parameter] public int RepoId { get; set; }

    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    private string _pattern = string.Empty;
    private bool _preventDeletion = true;
    private bool _preventForcePush = true;
    private BranchProtectionLevel _protectionLevel = BranchProtectionLevel.NotProtected;
    private List<OmniOption<BranchProtectionLevel>> _levels = [];

    protected override void OnInitialized() => _levels = BranchProtectionLevelOptions.Build(L);

    private async Task Save()
    {
        if (string.IsNullOrWhiteSpace(_pattern))
        {
            Toast.Error(L["Required"].Value, L["Pattern"].Value);
            return;
        }

        var request = new CreateBranchProtectionRuleRequest
        {
            Pattern = _pattern.Trim(),
            PreventDeletion = _preventDeletion,
            PreventForcePush = _preventForcePush,
            ProtectionLevel = _protectionLevel
        };

        var result = await Api.Git.CreateGitBranchProtectionRuleAsync(RepoId, request);
        if (result is not null)
        {
            Toast.Success(L["Saved"].Value, _pattern);
            Dialog.Close(true);
        }
    }
}
