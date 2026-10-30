// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.PortAllocation;

/// <summary>
/// The reads this module needs about a library's scope. They go straight to the shared entities
/// rather than through the owning modules: <c>VariableLibraries</c> and <c>Environments</c> both sit
/// below this one, and injecting them would invert the layering.
/// </summary>
public interface IPortAllocationRepository
{
    /// <summary>The library's owner triple, or null when the library does not exist.</summary>
    Task<LibraryScope?> FindLibraryScopeAsync(int libraryId, CancellationToken ct = default);

    /// <summary>The servers reachable from a library's scope, ordered by name.</summary>
    Task<List<(int ServerId, string ServerName)>> GetCandidateServersAsync(
        LibraryScope scope, CancellationToken ct = default);

    /// <summary>The library's existing <c>PORT_*</c> values that parse as ports.</summary>
    Task<List<int>> GetDeclaredPortEntriesAsync(int libraryId, CancellationToken ct = default);

    /// <summary>The library's entries among <paramref name="keys"/>, so an allocation can fill an
    /// entry that exists and is still empty instead of colliding with its unique key.</summary>
    Task<List<ExistingLibraryEntry>> FindEntriesByKeyAsync(
        int libraryId, IReadOnlyCollection<string> keys, CancellationToken ct = default);

    Task<bool> ServerExistsAsync(int serverId, CancellationToken ct = default);
}

/// <summary>An entry a library already holds under a key an allocation asks for.</summary>
public sealed record ExistingLibraryEntry(int Id, string Key, string Value);

/// <summary>
/// A library's owner, resolved to the project the reservations must be recorded under. Project is
/// null for an environment that belongs to none, which is the case allocation refuses.
/// </summary>
public sealed record LibraryScope(
    int LibraryId,
    string LibraryName,
    int? ProjectId,
    int? EnvironmentId,
    int? ProjectServerId,
    int? ProjectServerServerId);
