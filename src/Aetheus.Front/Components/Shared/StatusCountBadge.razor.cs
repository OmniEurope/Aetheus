// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Components.Shared;

public partial class StatusCountBadge
{
    /// <summary>How many items are in the status. Zero renders nothing.</summary>
    [Parameter] public int Count { get; set; }

    /// <summary>The localized status name: the visible text when wide, the accessible name and tooltip always.</summary>
    [Parameter, EditorRequired] public string Label { get; set; } = string.Empty;

    /// <summary>The status icon shown in place of the name below the phone width.</summary>
    [Parameter, EditorRequired] public OmniIconName Icon { get; set; }

    [Parameter] public OmniTone Tone { get; set; } = OmniTone.Neutral;

    [Parameter] public string? Class { get; set; }

    private string CountText => Count.ToString(CultureInfo.CurrentCulture);

    private string CssClass => string.IsNullOrWhiteSpace(Class) ? "status-count-badge" : $"status-count-badge {Class}";
}
