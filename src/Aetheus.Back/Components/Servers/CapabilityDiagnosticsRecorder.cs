// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Servers;

/// <summary>Persist and log capability diagnostics only when their stored value changes. Disabled
/// configured capabilities use Warning, while collection delays serving cached data use
/// Information.</summary>
internal static class CapabilityDiagnosticsRecorder
{
    public static void Apply(Server server, IReadOnlyList<string> diagnostics, ILogger logger)
    {
        var json = diagnostics.Count == 0 ? null : JsonSerializer.Serialize(diagnostics);
        if (string.Equals(server.CapabilityDiagnosticsJson, json, StringComparison.Ordinal)) return;

        var previous = ServerDataMapper.DeserializeDiagnostics(server.CapabilityDiagnosticsJson);
        server.CapabilityDiagnosticsJson = json;

        var (capability, collection) = Split(diagnostics);
        var (previousCapability, previousCollection) = Split(previous);
        if (!capability.SequenceEqual(previousCapability, StringComparer.Ordinal))
        {
            if (capability.Count > 0)
                logger.LogWarning("Server {ServerId} capability diagnostics: {Diagnostics}",
                    server.Id, string.Join("; ", capability));
            else
                logger.LogInformation("Server {ServerId} capability diagnostics cleared", server.Id);
        }

        if (collection.Count > 0 && !collection.SequenceEqual(previousCollection, StringComparer.Ordinal))
            logger.LogInformation("Server {ServerId} heartbeat collection diagnostics: {Diagnostics}",
                server.Id, string.Join("; ", collection));
    }

    private static (List<string> Capability, List<string> Collection) Split(IEnumerable<string> diagnostics)
    {
        var capability = new List<string>();
        var collection = new List<string>();
        foreach (var diagnostic in diagnostics)
            (HeartbeatCollectionDiagnostics.IsCollectionDiagnostic(diagnostic) ? collection : capability).Add(diagnostic);
        return (capability, collection);
    }
}
