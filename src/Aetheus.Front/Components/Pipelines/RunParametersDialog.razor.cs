// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Pipelines;

// P: queue-time run-parameters dialog. Returns the effective name→value map (string-encoded) via
// OmniDialogService.Close, or null on cancel. The inputs themselves live in RunParameterFields, shared
// with the unified launch dialog.
public partial class RunParametersDialog
{
    private readonly object _formModel = new();
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public List<PipelineRunParameterDto> Parameters { get; set; } = [];

    // S-UX-K7P2: optional seed values (e.g. the source run's parameters on re-run) that override the
    // declared defaults so the dialog opens prefilled with what the previous run used.
    [Parameter] public IReadOnlyDictionary<string, string>? Prefill { get; set; }
    [Parameter] public string? SubmitText { get; set; }
    [Parameter] public string SubmitIcon { get; set; } = "play_arrow";

    /// <summary>The project's restorable releases, passed through to the fields as a picking aid;
    /// <c>null</c> when no parameter takes a version.</summary>
    [Parameter] public List<ReleaseDto>? AvailableReleases { get; set; }

    /// <summary>How many releases the project can restore in all, when more exist than were loaded.</summary>
    [Parameter] public int AvailableReleaseTotal { get; set; }

    private RunParameterFields? _fields;

    private void OnSubmit()
    {
        // A refused submit shows its message under the field itself; nothing more to show here.
        if (_fields is null || !_fields.TryCollect(out var values, out _)) return;
        Dialog.Close(values);
    }

    private void OnCancel() => Dialog.Close(null);
}
