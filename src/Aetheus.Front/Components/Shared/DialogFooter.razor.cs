// SPDX-License-Identifier: EUPL-1.2

using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Components.Shared;

public partial class DialogFooter
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    /// <summary>Default the dismiss label to localized GoBack so shared dialog footers use
    /// consistent wording.</summary>
    [Parameter] public string? CancelText { get; set; }

    /// <summary>Cancel button variant.</summary>
    [Parameter] public OmniButtonVariant CancelVariant { get; set; } = OmniButtonVariant.Secondary;

    private OmniButtonVariant OmniCancelVariant => CancelVariant;

    /// <summary>Invoked when the user clicks Cancel - typically closes the dialog.</summary>
    [Parameter] public EventCallback OnCancel { get; set; }

    /// <summary>The dialog's primary action button(s), right-aligned next to Cancel.</summary>
    [Parameter] public RenderFragment? ChildContent { get; set; }

    /// <summary>
    /// Optional secondary actions (Copy, artifact/pipeline links…) rendered left-aligned, opposite
    /// the Cancel + primary cluster. When present the footer switches to space-between layout.
    /// </summary>
    [Parameter] public RenderFragment? SecondaryContent { get; set; }
}
