// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Releases;

/// <summary>Recette R-13: picks the status a release is put back to and why; closes with a
/// <see cref="RestoreReleaseStatusRequest"/>, or nothing when dismissed.</summary>
public partial class RestoreReleaseStatusDialog
{
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter] public string Version { get; set; } = string.Empty;
    [Parameter] public ReleaseStatus CurrentStatus { get; set; }

    private RestoreModel _model = new();

    /// <summary>The statuses the backend accepts, minus the current one.</summary>
    internal IReadOnlyList<ReleaseStatus> Targets =>
        ReleaseStatusRestore.Targets.Where(status => status != CurrentStatus).ToList();

    private IReadOnlyList<OmniOption<ReleaseStatus>> TargetOptions =>
        Targets.ToOptions(status => status, status => L.Localize(status));

    // The first target (Deployed, unless the release already is): the case this dialog exists for.
    protected override void OnInitialized() => _model = new RestoreModel { Status = Targets[0] };

    private bool _closed;
    private void Submit()
    {
        if (_closed) return;
        _closed = true;
        Dialog.Close(new RestoreReleaseStatusRequest { Status = _model.Status, Reason = _model.Reason.Trim() });
    }

    private sealed class RestoreModel
    {
        public ReleaseStatus Status { get; set; }

        [Required, StringLength(ReleaseStatusRestore.MaxReasonLength)]
        public string Reason { get; set; } = string.Empty;
    }
}
