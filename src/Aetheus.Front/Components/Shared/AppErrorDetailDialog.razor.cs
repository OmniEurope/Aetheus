// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

/// <summary>Read-only detail of one grouped app error, opened from <see
/// cref="AppErrorsView"/>.</summary>
public partial class AppErrorDetailDialog
{
    [Parameter, EditorRequired] public AppErrorEventDto Error { get; set; } = default!;

    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private ClipboardService Clipboard { get; set; } = default!;

    /// <summary>R-39: the whole detail as Markdown, written at the click (clipboard only, no file).</summary>
    private Task CopyMarkdownAsync() => Clipboard.CopyAsync(ErrorDetailMarkdown.ForAppError(Error, key => L[key]), L["MessageCopied"]);
}
