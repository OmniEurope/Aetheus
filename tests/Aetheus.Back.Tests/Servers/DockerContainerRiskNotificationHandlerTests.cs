// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Alerts;
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Components.Servers.Events;
using NSubstitute;

namespace Aetheus.Back.Tests.Servers;

/// <summary>PLAN-010 lot 1: a container risk is kept as an administrator notification and sent to the
/// mail and webhook channels, not only toasted to the browsers connected at that moment.</summary>
public sealed class DockerContainerRiskNotificationHandlerTests
{
    [Fact]
    public async Task HandleAsync_RecordsAnAdministratorNotification_AndPublishesAlertTriggered()
    {
        var userNotifications = Substitute.For<IUserNotificationService>();
        var notifications = Substitute.For<INotificationService>();
        var occurrences = Substitute.For<IAlertOccurrenceService>();
        occurrences.RecordAsync(Arg.Any<NewAlertOccurrence>(), Arg.Any<CancellationToken>()).Returns(42L);
        var handler = new DockerContainerRiskNotificationHandler(userNotifications, notifications, occurrences);

        await handler.HandleAsync(new DockerContainerRiskDetectedEvent(
            7, "web-1", "probe", "alpine:3.20", "privileged", "Critical", "message", false,
            new DateTime(2026, 10, 17, 8, 0, 0, DateTimeKind.Utc)), TestContext.Current.CancellationToken);

        // R-52: the alert is stored first and its identifier travels in the event, so the mail's button opens it.
        await occurrences.Received(1).RecordAsync(
            Arg.Is<NewAlertOccurrence>(occurrence => occurrence.ServerId == 7
                && occurrence.Metric == "DockerContainerRisk"
                && occurrence.Severity == AlertSeverity.Critical
                && occurrence.Message == "message"),
            Arg.Any<CancellationToken>());
        await notifications.Received(1).SendEventAsync(
            "alert.triggered",
            Arg.Is<object>(payload => System.Text.Json.JsonSerializer.Serialize(payload).Contains("\"AlertId\":42", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());

        await userNotifications.Received(1).RecordAdministratorEventAsync(
            "alert.triggered",
            "Sec-Audit: container probe has host access on server web-1",
            Arg.Is<string>(json => json.Contains("\"ContainerName\":\"probe\"", StringComparison.Ordinal)
                && json.Contains("\"Metric\":\"DockerContainerRisk\"", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
        await notifications.Received(1).SendEventAsync("alert.triggered", Arg.Any<object>(), Arg.Any<CancellationToken>());
    }
}
