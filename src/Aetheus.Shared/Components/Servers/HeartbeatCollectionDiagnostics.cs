// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Servers;

/// <summary> The capability-diagnostic lines the agent adds about its OWN heartbeat collection (a
/// collection that overran its budget, a collector still in flight from an earlier beat). They
/// describe a slow beat, not a missing capability, so the backend logs them at Information while
/// every other diagnostic (an unreadable sudoers drop-in, a blocked sudo, a failed deployment
/// probe) stays a warning. The texts are shared so the agent that writes them and the backend that
/// classifies them cannot drift. </summary>
public static class HeartbeatCollectionDiagnostics
{
    public const string CollectionTimedOut =
        "Heartbeat inventory collection timed out; cached or partial data reported";

    public const string QuarantinedPrefix = "Heartbeat collectors quarantined: ";

    public static string Quarantined(IEnumerable<string> collectorNames) =>
        QuarantinedPrefix + string.Join(", ", collectorNames);

    public static bool IsCollectionDiagnostic(string diagnostic) =>
        diagnostic is not null
        && (string.Equals(diagnostic, CollectionTimedOut, StringComparison.Ordinal)
            || diagnostic.StartsWith(QuarantinedPrefix, StringComparison.Ordinal));
}
