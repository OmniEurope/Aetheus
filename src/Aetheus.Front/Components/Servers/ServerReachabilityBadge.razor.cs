// SPDX-License-Identifier: EUPL-1.2
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Components.Servers;

/// <summary>
/// Shows, for an offline server, whether the backend's external TCP probe found the host up (only the
/// agent stopped), the host unreachable, or why the answer is unknown. A probe older than the last
/// heartbeat belongs to an earlier offline period and reads as "not probed yet", never as current.
/// </summary>
public partial class ServerReachabilityBadge
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public ServerDto Server { get; set; } = default!;

    /// <summary>Also writes the probed target and time under the badge (detail page).</summary>
    [Parameter] public bool ShowDetail { get; set; }

    // A server that never reported has no agent to tell apart from its host: nothing to show.
    private bool Visible => Server.Status == ServerStatus.Offline && Server.LastHeartbeat.Year >= 2000;

    internal ServerReachability Effective =>
        Server.ExternalProbeAt is { } probedAt && probedAt >= Server.LastHeartbeat
            ? Server.ExternalProbe
            : ServerReachability.NotProbed;

    private OmniIconName StatusIcon => Effective switch
    {
        ServerReachability.HostReachable => OmniIconName.PlugsConnected,
        ServerReachability.HostUnreachable => OmniIconName.Offline,
        _ => OmniIconName.Question
    };

    private OmniTone Tone => Effective switch
    {
        ServerReachability.HostReachable => OmniTone.Warning,
        ServerReachability.HostUnreachable => OmniTone.Danger,
        _ => OmniTone.Neutral
    };

    /// <summary>The full sentence (what the case means), then where and when the probe ran.</summary>
    internal string Detail
    {
        get
        {
            var hint = L[$"ServerReachabilityHint_{Effective}"].Value;
            if (Effective == ServerReachability.NotProbed || Server.ExternalProbeAt is not { } probedAt) return hint;
            var ago = RelativeTime.FormatAgo(L, probedAt);
            var probe = string.IsNullOrEmpty(Server.ExternalProbeTarget)
                ? string.Format(L["ServerReachabilityProbedAgo"], ago)
                : string.Format(L["ServerReachabilityProbedTargetAgo"], Server.ExternalProbeTarget, ago);
            return $"{hint} {probe}";
        }
    }
}
