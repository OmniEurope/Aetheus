// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Audit;

/// <summary>Column filters for audit logs and entity audit trails, including the timestamp
/// range.</summary>
internal static class AuditLogQuery
{
    internal static readonly GridQueryMap<AuditLog> Columns = new GridQueryMap<AuditLog>()
        .Date("timestamp", a => a.Timestamp)
        .Text("username", a => a.Username)
        .Text("action", a => a.Action)
        .Text("entityType", a => a.EntityType)
        .Number("entityId", a => a.EntityId)
        .Text("details", a => a.Details);
}
