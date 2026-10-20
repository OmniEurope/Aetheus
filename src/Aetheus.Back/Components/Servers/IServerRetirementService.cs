// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Servers;

/// <summary>Retirement preserves rows and links, revokes agent tokens and hides the server;
/// re-enrollment revives it, while explicit purge erases it. Callers enforce Server.Admin
/// permission.</summary>
public interface IServerRetirementService
{
    /// <summary>Retires an active server. Returns false when no active server has this id.</summary>
    Task<bool> RetireServerAsync(int id, string actorUsername, CancellationToken ct = default);

    Task<PaginatedResult<RetiredServerDto>> GetRetiredServersAsync(
        PaginationRequest request, List<int>? accessibleIds = null, CancellationToken ct = default);

    /// <summary>Permanently deletes a retired server and everything linked to it, its backup policies
    /// included. Returns false when the id is unknown; throws <see cref="ConflictException"/> for an
    /// active server, which must be retired first.</summary>
    Task<bool> PurgeServerAsync(int id, string actorUsername, CancellationToken ct = default);
}
