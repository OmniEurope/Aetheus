// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Servers;

/// <summary>
/// Recette R2-111: what the Overview says under the agent version once an update was asked. In
/// progress (the button busy and "Update in progress to x.y.z") from the confirmation until the agent
/// reports the target version or the request fails; failed, with the reason, when it did.
/// <para>
/// Read from what the page already holds: the request this view just queued (target and id), and the
/// server's persisted <see cref="ServerDetailDto.AgentUpdateRequest"/>, which the detail loader reloads
/// on every agent-update event and which keeps a running update visible after a reload.
/// </para>
/// </summary>
internal sealed record AgentUpdateOverviewState(string? InProgressTarget, bool Failed, string? FailureDiagnostic)
{
    internal static readonly AgentUpdateOverviewState None = new(null, false, null);

    internal bool InProgress => InProgressTarget is not null;

    internal static AgentUpdateOverviewState Read(ServerDetailDto server, string? requestedTarget, int? requestedRequestId)
    {
        ArgumentNullException.ThrowIfNull(server);
        var request = server.AgentUpdateRequest;
        // The persisted request speaks once it is the one this view queued, or a newer one.
        if (request is not null && (requestedRequestId is null || request.RequestId >= requestedRequestId))
            return FromPersisted(server, request, askedHere: requestedTarget is not null);

        return requestedTarget is not null && !Reached(server, requestedTarget)
            ? new AgentUpdateOverviewState(requestedTarget, false, null)
            : None;
    }

    private static AgentUpdateOverviewState FromPersisted(
        ServerDetailDto server, AgentUpdateRequestSummaryDto request, bool askedHere) => request.Status switch
        {
            // An older failure is the progress card's to show; the Overview reports the one it asked for.
            AgentUpdateRequestStatus.Failed => askedHere ? new AgentUpdateOverviewState(null, true, request.FailureDiagnostic) : None,
            AgentUpdateRequestStatus.Confirmed => None,
            _ when Reached(server, request.TargetVersion) => None,
            _ => new AgentUpdateOverviewState(request.TargetVersion, false, null)
        };

    private static bool Reached(ServerDetailDto server, string target) =>
        string.IsNullOrWhiteSpace(target) || string.Equals(server.AgentVersion, target, StringComparison.OrdinalIgnoreCase);
}
