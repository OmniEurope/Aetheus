// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Agent.Core.Services;

/// <summary>
/// The heartbeat inventories the agent keeps for a while instead of collecting them on every beat
/// (<see cref="Configuration.AgentRuntimeDefaults.SlowInventoryTtl"/>, Docker's shorter TTL). An operation that
/// changes what one of them reports marks it outdated (<see cref="AgentRuntimeHealth.InvalidateInventories"/>),
/// so the next beat collects it again and the page shows the result of the operation, not the state of up to
/// three minutes before (recette R-119).
/// </summary>
[Flags]
public enum CachedInventory
{
    None = 0,
    Apache = 1,
    Mail = 2,
    Certbot = 4,
    Teamspeak = 8,
    Portsentry = 16,
    Rkhunter = 32,
    Firewall = 64,
    SecurityUpdates = 128,
    Docker = 256
}

/// <summary>Which cached inventory an operation changes.</summary>
internal static class CachedInventoryOperations
{
    /// <summary>The inventories the completed <paramref name="operation"/> may have changed. An operation that
    /// only reads (a log, a queue, a configuration test) changes none.</summary>
    internal static CachedInventory ChangedBy(OperationKind operation)
    {
        if (ReadOnly.Contains(operation)) return CachedInventory.None;
        if (operation == OperationKind.SystemPackageUpgrade) return CachedInventory.SecurityUpdates;
        // A project deletion removes its containers, its sites and its certificates (ADR-058); a deployment starts
        // or replaces containers.
        if (operation == OperationKind.ProjectResourcesRemove)
            return CachedInventory.Docker | CachedInventory.Apache | CachedInventory.Certbot;
        if (operation == OperationKind.PipelineDeploy) return CachedInventory.Docker;
        var name = operation.ToString();
        foreach (var (prefix, inventory) in ByOperationPrefix)
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal))
                return inventory;
        }
        return CachedInventory.None;
    }

    private static readonly (string Prefix, CachedInventory Inventory)[] ByOperationPrefix =
    [
        ("Apache", CachedInventory.Apache),
        ("Mail", CachedInventory.Mail),
        ("Certbot", CachedInventory.Certbot),
        ("Teamspeak", CachedInventory.Teamspeak),
        ("Portsentry", CachedInventory.Portsentry),
        ("Rkhunter", CachedInventory.Rkhunter),
        ("Firewall", CachedInventory.Firewall),
        ("Docker", CachedInventory.Docker),
        ("BlueGreen", CachedInventory.Docker)
    ];

    private static readonly HashSet<OperationKind> ReadOnly =
    [
        OperationKind.ApacheTestConfig, OperationKind.ApacheGetLogs, OperationKind.ApacheGetConfig,
        OperationKind.ApacheGetHtaccess,
        OperationKind.MailViewQueue, OperationKind.MailGetLogs, OperationKind.MailTestConfig,
        OperationKind.MailQuotaReport, OperationKind.MailOutboundProbe, OperationKind.MailLeftoversCheck,
        OperationKind.PortsentryStatus, OperationKind.PortsentryGetLogs,
        OperationKind.TeamspeakGetLogs
    ];
}
