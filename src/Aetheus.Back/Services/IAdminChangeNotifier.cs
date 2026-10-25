// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Services;

// Admin entity-name constants moved to Aetheus.Shared.Components.Shared.AdminEntities (S-TECH-RT4M) so the
// backend broadcast and the frontend subscription filter share one case-sensitive contract. A global
// using alias keeps the unqualified `AdminEntities.X` references across the backend working.

public interface IAdminChangeNotifier
{
    Task BroadcastAsync(string entity, int id, string op, CancellationToken ct = default);
}
