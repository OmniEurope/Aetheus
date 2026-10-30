// SPDX-License-Identifier: EUPL-1.2
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Components;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Components.Shared;

/// <summary>
/// The icon of a text badge (recette R-44 and R-59, user rule of 2026-10-25): a badge whose text has no
/// number carries an icon, a badge that carries a number has none (it is a <see cref="NumberBadge"/>),
/// a one-letter grade is a filled badge without icon (see <see cref="GradeBadge"/>, R-107). The icons reuse the ones the
/// pipeline run statuses already use: Check for success, Close for a failure, Warning for attention.
/// </summary>
internal static class BadgeIcon
{
    private static readonly ConcurrentDictionary<OmniIconName, RenderFragment> Fragments = new();

    /// <summary>The icon of a badge that names a thing (a kind, a type, a tag, a verdict): the tone's icon,
    /// a generic tag for the neutral and accent tones.</summary>
    public static RenderFragment For(OmniTone tone) => Of(NameFor(tone));

    /// <summary>The icon of a badge that reports a state (active, stopped, installed...): same as
    /// <see cref="For"/>, except that the neutral tone is the "off" state (Prohibit) and the accent tone
    /// the "on" state (Check).</summary>
    public static RenderFragment ForState(OmniTone tone) => Of(StateNameFor(tone));

    /// <summary>The icon fragment of one OE icon, ready for <c>OmniBadge.Icon</c>.</summary>
    public static RenderFragment Of(OmniIconName name) => Fragments.GetOrAdd(name, static icon => builder =>
    {
        builder.OpenComponent<OmniIcon>(0);
        builder.AddComponentParameter(1, nameof(OmniIcon.Name), icon);
        builder.CloseComponent();
    });

    internal static OmniIconName NameFor(OmniTone tone) => tone switch
    {
        OmniTone.Success => OmniIconName.Check,
        OmniTone.Danger => OmniIconName.Close,
        OmniTone.Warning => OmniIconName.Warning,
        OmniTone.Info => OmniIconName.Info,
        _ => OmniIconName.Tag
    };

    internal static OmniIconName StateNameFor(OmniTone tone) => tone switch
    {
        OmniTone.Accent => OmniIconName.Check,
        OmniTone.Neutral => OmniIconName.Prohibit,
        _ => NameFor(tone)
    };
}
