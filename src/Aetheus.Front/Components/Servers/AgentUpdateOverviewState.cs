// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Servers;

/// <summary>Derives overview update progress and failure from the locally queued request and
/// persisted <see cref="ServerDetailDto.AgentUpdateRequest"/>. Persisted state keeps an active
/// update visible after reloading.</summary>
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
