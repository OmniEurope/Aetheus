// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Shared;

namespace Aetheus.Back.Tests.Mail;

/// <summary>The outbound delivery facts of a heartbeat survive persistence and come back on the server
/// detail; an agent that reports none leaves the column empty.</summary>
public sealed class MailStateProjectorDeliveryTests
{
    [Fact]
    public void Delivery_RoundTripsThroughThePersistedState()
    {
        var mail = new MailDataDto
        {
            IsInstalled = true,
            Delivery = new MailDeliveryStateDto
            {
                InetProtocols = "ipv4",
                MinimalBackoffTime = "60s",
                MaximalBackoffTime = "900s",
                QueueRunDelay = "60s",
                HostIpv6Addresses = ["2001:db8::25"],
                Deferred = [new MailDeferredMessageDto { QueueId = "4F2A1B3C9D", Recipient = "bob@gmail.com", Reason = "421-4.7.28" }]
            }
        };

        var state = MailStateProjector.ToState(7, mail);
        var back = MailStateProjector.ToDto(state).Delivery;

        Assert.NotNull(state?.DeliveryJson);
        Assert.Equal(mail.Delivery.HostIpv6Addresses, back.HostIpv6Addresses);
        Assert.Equal(("ipv4", "60s", "900s", "60s"), (back.InetProtocols, back.MinimalBackoffTime, back.MaximalBackoffTime, back.QueueRunDelay));
        Assert.Equal("421-4.7.28", Assert.Single(back.Deferred).Reason);
    }

    [Fact]
    public void Delivery_NothingReported_StoresNothing()
    {
        var state = MailStateProjector.ToState(7, new MailDataDto { IsInstalled = true });

        Assert.Null(state?.DeliveryJson);
        Assert.True(MailStateProjector.ToDto(state).Delivery.IsEmpty);
    }
}
