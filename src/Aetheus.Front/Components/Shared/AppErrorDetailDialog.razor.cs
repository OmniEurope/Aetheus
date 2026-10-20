// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

/// <summary>Read-only detail of one grouped app error, opened from <see
/// cref="AppErrorsView"/>.</summary>
public partial class AppErrorDetailDialog
{
    [Parameter, EditorRequired] public AppErrorEventDto Error { get; set; } = default!;

    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
}
