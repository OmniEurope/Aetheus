// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Servers;

/// <summary>
/// A failed agent update stays the server's latest request until another one is asked for. Once the agent reports a
/// version at or past the failed target (reinstalled, or updated by hand), that failure no longer describes the server:
/// showing it would tell the user an update is still broken on an agent that already runs a newer version. Only a
/// failure is dropped, and only when both versions read as versions; anything else is kept as it is.
/// </summary>
internal static class SupersededAgentUpdateFailure
{
    internal static AgentUpdateRequestSummaryDto? Drop(AgentUpdateRequestSummaryDto? request, string? agentVersion) =>
        request is not null && IsSuperseded(request.Status, request.TargetVersion, agentVersion) ? null : request;

    internal static bool IsSuperseded(AgentUpdateRequestStatus status, string? targetVersion, string? agentVersion) =>
        status == AgentUpdateRequestStatus.Failed
        && TryReadVersion(agentVersion, out var running)
        && TryReadVersion(targetVersion, out var target)
        && running >= target;

    // "1.6.0", "1.6.0+abc" or "1.6.0-rc.1": the numeric part only, so a build suffix never hides the comparison.
    private static bool TryReadVersion(string? text, out Version version)
    {
        version = new Version();
        if (string.IsNullOrWhiteSpace(text)) return false;
        var end = text.IndexOfAny(['+', '-', ' ']);
        return Version.TryParse(end < 0 ? text : text[..end], out version!);
    }
}
