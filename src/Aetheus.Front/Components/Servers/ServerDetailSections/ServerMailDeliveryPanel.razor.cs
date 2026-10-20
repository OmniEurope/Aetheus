// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary>Read-only view of the outbound delivery facts the agent reports: the server IPv4 address, the global
/// IPv6 addresses of the host (so one can see whether it has IPv6 at all), the Postfix IP policy, the retry
/// timings and the deferred queue entries with their reason.</summary>
public partial class ServerMailDeliveryPanel
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public MailDeliveryStateDto Delivery { get; set; } = default!;
    [Parameter] public string HostIpv4 { get; set; } = string.Empty;

    private IReadOnlyList<string> Ipv6 => Delivery.HostIpv6Addresses;

    private string Ipv6Text => Ipv6.Count == 0 ? L["MailDeliveryNoIpv6"].Value : string.Join(", ", Ipv6);

    private string RetryText =>
        string.IsNullOrEmpty(Delivery.MinimalBackoffTime) && string.IsNullOrEmpty(Delivery.MaximalBackoffTime)
            && string.IsNullOrEmpty(Delivery.QueueRunDelay)
            ? L["MailDeliveryNotReported"].Value
            : $"{Delivery.MinimalBackoffTime} / {Delivery.MaximalBackoffTime} / {Delivery.QueueRunDelay}";

    private string Display(string value) => string.IsNullOrEmpty(value) ? L["MailDeliveryNotReported"].Value : value;
}
