// SPDX-License-Identifier: EUPL-1.2

using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>A tile of the Capabilities step (R-22): an OE selectable card with a short title and description, and a "?" that opens
/// a dialog with the longer explanation. Alone it is an option that is on or off (a check box card, <see cref="Value"/>); with a
/// <see cref="Choice"/> it is one choice of the surrounding <c>OmniSelectableCardGroup</c>, which binds the selection.</summary>
public partial class PipelineSetupCapabilityTile
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;

    /// <summary>The capability's name in the wizard's address; the tile is identified by it in the page.</summary>
    [Parameter, EditorRequired] public string Key { get; set; } = string.Empty;
    [Parameter, EditorRequired] public OmniIconName Icon { get; set; }
    [Parameter, EditorRequired] public string Title { get; set; } = string.Empty;

    /// <summary>One short line, which fits the tile whatever its width.</summary>
    [Parameter, EditorRequired] public string Description { get; set; } = string.Empty;

    /// <summary>What the "?" tells: the longer explanation, for a reader who needs more than the line.</summary>
    [Parameter, EditorRequired] public string Help { get; set; } = string.Empty;

    [Parameter] public bool Value { get; set; }
    [Parameter] public EventCallback<bool> ValueChanged { get; set; }

    /// <summary>The tile keeps its state and ignores clicks, as a choice another one forces off (R-23).</summary>
    [Parameter] public bool Disabled { get; set; }

    /// <summary>Text and badges under the description (why a tile is greyed, why it is unticked).</summary>
    [Parameter] public RenderFragment? Note { get; set; }

    /// <summary>Set, the tile stands for this choice of the surrounding card group instead of an option of its own.</summary>
    [Parameter] public object? Choice { get; set; }

    /// <summary>An option tile that cannot be changed because of what it depends on (R-23), greyed beyond the OE disabled state.</summary>
    private bool Waiting => Disabled && Choice is null;

    private RenderFragment Card => builder =>
    {
        builder.OpenComponent<OmniSelectableCard>(0);
        if (Choice is null)
        {
            builder.AddAttribute(1, nameof(OmniSelectableCard.Multiple), true);
            builder.AddAttribute(2, nameof(OmniSelectableCard.Value), Value);
            builder.AddAttribute(3, nameof(OmniSelectableCard.ValueChanged), ValueChanged);
        }
        else
        {
            builder.AddAttribute(4, nameof(OmniSelectableCard.Choice), Choice);
        }
        builder.AddAttribute(5, nameof(OmniSelectableCard.Title), Title);
        builder.AddAttribute(6, nameof(OmniSelectableCard.Description), Description);
        builder.AddAttribute(7, nameof(OmniSelectableCard.Disabled), Disabled);
        builder.AddAttribute(8, nameof(OmniSelectableCard.Icon), (RenderFragment)(iconBuilder =>
        {
            iconBuilder.OpenComponent<OmniIcon>(0);
            iconBuilder.AddAttribute(1, nameof(OmniIcon.Name), Icon);
            iconBuilder.CloseComponent();
        }));
        builder.AddAttribute(9, "data-testid", $"setup-option-{Key}");
        // Always written, even null: the card keeps the content it was last given when none is supplied.
        builder.AddAttribute(10, nameof(OmniSelectableCard.ChildContent), Note);
        builder.CloseComponent();
    };

    private Task ShowHelpAsync() =>
        Dialog.Alert(Help, Title, new OmniAlertOptions { OkButtonText = L["Close"].Value });
}
