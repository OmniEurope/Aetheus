// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary> Picks the new owner of the pipelines of <see cref="Scope"/>, previews the transfer,
/// then applies it. Closes with the <see cref="PipelineOwnerTransferResultDto"/> of the transfer,
/// or nothing when dismissed. </summary>
public partial class PipelineOwnerTransferDialog
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    /// <summary>The pipelines concerned: one of PipelineId, ProjectId or CurrentOwnerUsername.</summary>
    [Parameter, EditorRequired] public PipelineOwnerTransferRequest Scope { get; set; } = new();

    /// <summary>Sentence shown above the choice (which pipelines, whose).</summary>
    [Parameter] public string? Description { get; set; }

    /// <summary>The user proposed first, previewed on open.</summary>
    [Parameter] public string? DefaultOwner { get; set; }

    /// <summary>The users that can be chosen (active users); loaded when not given.</summary>
    [Parameter] public List<PipelineOwnerCandidateDto> Candidates { get; set; } = [];

    /// <summary>When set, the dialog offers to extend a one-pipeline scope to every pipeline of this project.</summary>
    [Parameter] public int? ProjectId { get; set; }

    [Parameter] public string? ProjectName { get; set; }

    private bool _wholeProject;
    private string? _selected;

    private PipelineOwnerTransferRequest EffectiveScope => _wholeProject && ProjectId is { } projectId
        ? new PipelineOwnerTransferRequest { ProjectId = projectId }
        : Scope;
    private PipelineOwnerTransferResultDto? _preview;
    private bool _loadingPreview;
    private bool _transferring;
    private string? _error;

    private List<PipelineOwnerTransferItemDto> MissingAdmin =>
        _preview?.Items.Where(item => item.NewOwnerHoldsServerAdmin == false).ToList() ?? [];

    private List<PipelineOwnerTransferItemDto> Unresolved =>
        _preview?.Items.Where(item => item.NewOwnerHoldsServerAdmin is null).ToList() ?? [];

    private bool CanConfirm => !_transferring && !_loadingPreview && _preview is { Items.Count: > 0 }
        && string.Equals(_preview.NewOwnerUsername, _selected, StringComparison.Ordinal);

    protected override async Task OnInitializedAsync()
    {
        try
        {
            if (Candidates.Count == 0)
                Candidates = await Api.Pipelines.GetOwnerCandidatesAsync();
        }
        catch (HttpRequestException)
        {
            // 401 on an expired JWT: the redirect is handled by the auth provider.
            return;
        }

        if (!string.IsNullOrWhiteSpace(DefaultOwner) && Candidates.Any(c => c.Username == DefaultOwner))
            await OnTargetChangedAsync(DefaultOwner);
    }

    private async Task OnTargetChangedAsync(string? username)
    {
        _selected = username;
        _preview = null;
        _error = null;
        if (string.IsNullOrWhiteSpace(username)) return;
        _loadingPreview = true;
        try
        {
            var outcome = await Api.Pipelines.PreviewOwnerTransferAsync(EffectiveScope with { NewOwnerUsername = username });
            // The choice moved on while this preview was loading.
            if (!string.Equals(_selected, username, StringComparison.Ordinal)) return;
            _preview = outcome.Value;
            _error = outcome.Value is null ? outcome.Error?.Message ?? L["PipelineOwnerTransferFailed"].Value : null;
        }
        finally
        {
            _loadingPreview = false;
        }
    }

    private async Task OnWholeProjectChangedAsync(bool value)
    {
        _wholeProject = value;
        await OnTargetChangedAsync(_selected);
    }

    private async Task OnConfirmAsync()
    {
        if (!CanConfirm || _selected is null) return;
        _transferring = true;
        _error = null;
        try
        {
            var outcome = await Api.Pipelines.TransferOwnerAsync(EffectiveScope with { NewOwnerUsername = _selected });
            if (outcome.Value is { } result)
            {
                Toast.Success("Saved", "PipelineOwnerTransferred", result.Items.Count, result.NewOwnerUsername);
                Dialog.Close(result);
                return;
            }
            _error = outcome.Error?.Message ?? L["PipelineOwnerTransferFailed"].Value;
        }
        finally
        {
            _transferring = false;
        }
    }
}
