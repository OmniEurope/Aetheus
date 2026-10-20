// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Notifications;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages.Notifications;

/// <summary>
/// Recette R3-16: a finished AI task is announced in the inbox only. Its row carries no e-mail outcome,
/// so its status badge must not read as an orange "e-mail not configured".
/// </summary>
public sealed class NotificationInboxOnlyStatusTests
{
    [Fact]
    public void AnInboxOnlyEvent_GetsANeutralBadge_WhateverItsEmailStatus()
    {
        var delivery = new NotificationDeliveryDto
        {
            EventType = NotificationEventTypes.AiTaskCompleted,
            Status = NotificationDeliveryStatus.NotConfigured
        };

        Assert.True(NotificationLabels.IsInboxOnly(delivery.EventType));
        Assert.Equal(OmniTone.Neutral, NotificationLabels.StatusVariant(delivery));
    }

    [Fact]
    public void AMailedEvent_KeepsItsEmailStatusBadge()
    {
        var delivery = new NotificationDeliveryDto
        {
            EventType = NotificationEventTypes.PipelineFailed,
            Status = NotificationDeliveryStatus.NotConfigured
        };

        Assert.False(NotificationLabels.IsInboxOnly(delivery.EventType));
        Assert.Equal(OmniTone.Warning, NotificationLabels.StatusVariant(delivery));
    }
}
