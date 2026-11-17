// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Shared;

/// <summary>The "online" counter and its help; see the razor file for the three states it tells apart.</summary>
public partial class OnlineAudienceValue
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    /// <summary>The distinct visitors seen in the window; read only when <see cref="State"/> is measured.</summary>
    [Parameter] public int? Count { get; set; }

    [Parameter] public OnlineAudienceState State { get; set; }

    /// <summary>When the last audience event was ingested (UTC), told in the help of a waiting counter.</summary>
    [Parameter] public DateTime? LastEventAt { get; set; }

    /// <summary>Write "3 online" instead of the bare number (a tile, not a grid cell).</summary>
    [Parameter] public bool Labeled { get; set; }

    /// <summary>Draw the « ? » help; the dashboard's monitored applications grid hides it (user request 2026-11-10).</summary>
    [Parameter] public bool ShowHelp { get; set; } = true;

    private string CountText => Labeled
        ? string.Format(L["OnlineUsers"], Count ?? 0)
        : (Count ?? 0).ToString(System.Globalization.CultureInfo.CurrentCulture);

    private string HelpText => State == OnlineAudienceState.Waiting
        ? $"{L["OnlineUsersHelp"]} {OnlineAudienceText.WaitingHint(L, LastEventAt)}"
        : L["OnlineUsersHelp"];
}
