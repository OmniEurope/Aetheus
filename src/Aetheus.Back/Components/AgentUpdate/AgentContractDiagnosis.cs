// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Back.Components.AgentUpdate;

/// <summary>A Pending task together with what its target server's agent declared, as stored from its
/// heartbeat.</summary>
public sealed record PendingTaskAgentContext(
    int TaskId,
    OperationKind Operation,
    string ServerName,
    string AgentVersion,
    int? AgentProtocolVersion,
    string? AgentCapabilitiesJson);

/// <summary>Why the agent of a server can never claim a given task, and the failure code and
/// (French) reason the task is failed with.</summary>
public sealed record AgentContractGap(string FailureCode, string Reason);

/// <summary>
/// Explains the silent case of the task claim: a Pending task whose operation needs a capability or a
/// protocol the server's agent does not declare is skipped by the claim forever. The test is the claim's
/// own (<see cref="AgentCapabilities.IsDeclared"/>, <see cref="AgentProtocol.IsSupported"/>), so a task
/// is diagnosed exactly when the claim would skip it.
/// </summary>
internal static class AgentContractDiagnosis
{
    public static AgentContractGap? Diagnose(PendingTaskAgentContext task, AgentReleaseManifestDto release)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(release);

        // Every operation maps to a capability (guarded by an architecture test); an unmapped one has
        // nothing to say about the agent.
        if (AgentCapabilities.RequiredFor(task.Operation) is not { } capability)
            return null;

        var protocolSupported = task.AgentProtocolVersion is { } protocol && AgentProtocol.IsSupported(protocol);
        if (protocolSupported && AgentCapabilities.IsDeclared(task.AgentCapabilitiesJson, capability))
            return null;

        var agent = string.IsNullOrWhiteSpace(task.AgentVersion) ? "version inconnue" : task.AgentVersion;
        var subject = $"L'agent du serveur « {task.ServerName} » ({agent})";
        var target = release.SoftwareVersion;
        const string update = "depuis la page du serveur (action « Mettre à jour »), puis relancez";

        string reason;
        if (!protocolSupported)
        {
            var declared = task.AgentProtocolVersion?.ToString(CultureInfo.InvariantCulture) ?? "inconnu";
            reason = $"{subject} utilise le protocole {declared}, que ce serveur ne prend plus en charge "
                + $"(protocole attendu : {release.MinimumSupportedProtocol} à {release.MaximumSupportedProtocol}) : "
                + $"la tâche ({task.Operation}) ne peut pas lui être confiée. Mettez l'agent à jour en {target} {update}.";
        }
        else if (release.SoftwareCapabilities.Contains(capability, StringComparer.Ordinal))
        {
            reason = string.Equals(task.AgentVersion, target, StringComparison.Ordinal)
                ? $"{subject} ne déclare pas la capacité « {capability} » requise par l'opération {task.Operation}, "
                    + $"alors qu'il est déjà en version cible : réinstallez l'agent {update}."
                : $"{subject} ne sait pas exécuter cette tâche (opération {task.Operation}, capacité « {capability} » absente) : "
                    + $"mettez-le à jour en {target} {update}.";
        }
        else
        {
            reason = $"{subject} ne déclare pas la capacité « {capability} » requise par l'opération {task.Operation}. "
                + "Cette capacité est un droit à accorder sur le serveur (la version cible de l'agent ne la fournit pas) : "
                + "accordez-la depuis la page du serveur, puis relancez.";
        }

        return new AgentContractGap(TaskFailureCodes.AgentIncompatible, reason);
    }
}
