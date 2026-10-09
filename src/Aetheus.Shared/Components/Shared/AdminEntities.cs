// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Shared;

/// <summary>
/// Logical entity names broadcast over the admin realtime hub (<c>AdminEntityChanged(entity, id, op)</c>).
/// Shared between backend (broadcast) and frontend (subscription filter) so the case-sensitive string
/// contract can never drift between the two sides (S-TECH-RT4M).
/// </summary>
public static class AdminEntities
{
    public const string User = "User";
    public const string Role = "Role";
    public const string Organization = "Organization";
    public const string Plugin = "Plugin";
    public const string RegistrationToken = "RegistrationToken";
    public const string PackageFeed = "PackageFeed";
    public const string PackageRegistry = "PackageRegistry";
    /// <summary>A new audit entry (recette R-181: the audit page follows it live).</summary>
    public const string AuditLog = "AuditLog";
    /// <summary>The system log files changed (recette R-181: the system logs page follows them live).</summary>
    public const string SystemLog = "SystemLog";

    /// <summary>New API timing samples reached the Performance page's recorder (the id is always 0).</summary>
    public const string ApiPerformance = "ApiPerformance";

    /// <summary>New API timing samples reached the OmniEurope.Performance recorder of the side-by-side
    /// trial (the id is always 0).</summary>
}
