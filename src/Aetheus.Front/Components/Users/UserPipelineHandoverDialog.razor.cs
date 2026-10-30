// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Users;

/// <summary> The confirmation of a user deactivation or deletion. When the user owns pipelines it
/// lists them and asks who takes them over (the acting administrator by default), since an inactive
/// owner gets every chained run refused. Closes with a <see cref="UserPipelineHandover"/>, or
/// nothing when dismissed. </summary>
public partial class UserPipelineHandoverDialog
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public string Username { get; set; } = string.Empty;

    /// <summary>The confirmation sentence of the operation.</summary>
    [Parameter] public string Message { get; set; } = string.Empty;

    /// <summary>True for a deletion, false for a deactivation.</summary>
    [Parameter] public bool Delete { get; set; }

    /// <summary>The user proposed to take the pipelines over: the acting administrator.</summary>
    [Parameter] public string? DefaultTarget { get; set; }

    private List<PipelineOwnershipDto> _owned = [];
    private List<PipelineOwnerCandidateDto> _candidates = [];
    private List<PipelineOwnerTransferItemDto> _missingAdmin = [];
    private string? _target;
    private bool _force;
    private bool _loading = true;
    private bool _previewing;
    private int _previewSequence;

    /// <summary>R1-53: the reason a read or the preview failed; the confirmation stays refused while set.</summary>
    private string? _error;

    /// <summary>The read of the owned pipelines is said only once it has lasted <see cref="LoadingNoticeDelay"/>: a
    /// quick read showing then removing its line made the dialog jump, which read as a second dialog.</summary>
    private bool _loadingNoticeDue;

    internal static readonly TimeSpan LoadingNoticeDelay = TimeSpan.FromMilliseconds(400);

    /// <summary>R1-53: the visible phase of the dialog while it waits on the API, null when idle.</summary>
    private string? Phase => _loading ? (_loadingNoticeDue ? L["UserHandoverLoadingPipelines"].Value : null)
        : _previewing ? L["UserHandoverCheckingRights"].Value
        : null;

    /// <summary>
    /// The handover is confirmable only once the reads are complete and sound: with pipelines owned, the
    /// preview of the chosen new owner must have finished too (a pending or failed one refuses).
    /// </summary>
    private bool CanConfirm => !_loading && !_previewing && _error is null
        && (_owned.Count == 0 || _force || !string.IsNullOrWhiteSpace(_target));

    protected override async Task OnInitializedAsync()
    {
        _ = SayLoadingWhenSlowAsync();
        try
        {
            var owned = Api.Pipelines.GetOwnedPipelinesAsync(Username);
            var candidates = Api.Pipelines.GetOwnerCandidatesAsync();
            _owned = await owned;
            _candidates = (await candidates).Where(candidate => candidate.Username != Username).ToList();
        }
        catch (HttpRequestException)
        {
            // A 401 on an expired JWT is redirected by the auth provider; any other refusal is said here
            // and never lets the confirmation through on an unread list.
            _error = L["UserHandoverLoadFailed"].Value;
            return;
        }
        finally
        {
            _loading = false;
        }

        if (_owned.Count > 0 && _candidates.Any(candidate => candidate.Username == DefaultTarget))
            await OnTargetChangedAsync(DefaultTarget);
    }

    private async Task SayLoadingWhenSlowAsync()
    {
        await Task.Delay(LoadingNoticeDelay);
        if (!_loading) return;
        _loadingNoticeDue = true;
        await InvokeAsync(StateHasChanged);
    }

    private async Task OnTargetChangedAsync(string? username)
    {
        var sequence = ++_previewSequence;
        _target = username;
        _missingAdmin = [];
        _error = null;
        if (string.IsNullOrWhiteSpace(username))
        {
            _previewing = false;
            return;
        }

        _previewing = true;
        try
        {
            var outcome = await Api.Pipelines.PreviewOwnerTransferAsync(new PipelineOwnerTransferRequest
            {
                CurrentOwnerUsername = Username,
                NewOwnerUsername = username,
            });
            // The choice moved on while this preview was loading: the newer one owns the state.
            if (sequence != _previewSequence) return;
            if (outcome.Value is { } preview)
                _missingAdmin = preview.Items.Where(item => item.NewOwnerHoldsServerAdmin == false).ToList();
            else
                _error = outcome.Error?.Message ?? L["UserHandoverPreviewFailed"].Value;
        }
        catch (HttpRequestException)
        {
            if (sequence == _previewSequence) _error = L["UserHandoverPreviewFailed"].Value;
        }
        finally
        {
            if (sequence == _previewSequence) _previewing = false;
        }
    }

    private void OnForceChanged(bool value) => _force = value;

    private void OnConfirm()
    {
        if (!CanConfirm) return;
        Dialog.Close(_owned.Count == 0
            ? new UserPipelineHandover(null, false)
            : new UserPipelineHandover(_force ? null : _target, _force));
    }
}
