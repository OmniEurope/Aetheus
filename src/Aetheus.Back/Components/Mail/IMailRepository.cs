// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Mail;

public interface IMailRepository
{
    Task<MailState?> GetStateAsync(int serverId, CancellationToken ct = default);
    Task<List<MailDomain>> GetDomainsAsync(int serverId, CancellationToken ct = default);
    Task<(List<MailDomain> Items, int Total)> GetDomainsPagedAsync(
        int serverId, string? search, int page, int pageSize, string? sortBy, bool sortDescending,
        CancellationToken ct = default, IReadOnlyList<GridFilter>? filters = null);
    Task<MailDomain?> GetDomainAsync(int serverId, int domainId, CancellationToken ct = default);
    Task<List<MailAccount>> GetAccountsAsync(int serverId, CancellationToken ct = default);
    Task<(List<MailAccount> Items, int Total)> GetAccountsPagedAsync(
        int serverId, string? search, int page, int pageSize, string? sortBy, bool sortDescending,
        CancellationToken ct = default, IReadOnlyList<GridFilter>? filters = null);
    Task<MailAccount?> GetAccountAsync(int accountId, CancellationToken ct = default);
    /// <summary>The server's domain of that name (case-insensitive, tracked), or null.</summary>
    Task<MailDomain?> FindDomainByNameAsync(int serverId, string name, CancellationToken ct = default);
    /// <summary>The mailbox with that address on any server (case-insensitive, tracked, with its domain), or null.</summary>
    Task<MailAccount?> FindAccountByEmailAsync(string email, CancellationToken ct = default);
    Task AddDomainAsync(MailDomain domain, CancellationToken ct = default);
    Task UpdateDomainAsync(MailDomain domain, CancellationToken ct = default);
    Task DeleteDomainAsync(MailDomain domain, CancellationToken ct = default);
    Task AddAccountAsync(MailAccount account, CancellationToken ct = default);
    Task UpdateAccountAsync(MailAccount account, CancellationToken ct = default);
    Task DeleteAccountAsync(MailAccount account, CancellationToken ct = default);
    /// <summary>Recette R3-49: the server's mailbox rows the heartbeat marked absent (<c>MissingSince</c> set), tracked.</summary>
    Task<List<MailAccount>> GetMissingAccountsAsync(int serverId, CancellationToken ct = default);
    Task DeleteAccountsAsync(IReadOnlyCollection<MailAccount> accounts, CancellationToken ct = default);
    Task AddTaskAsync(ServerTask task, CancellationToken ct = default);

    /// <summary>Marks the quota resync of this server as started; true only for the one caller that found it
    /// not yet done on a helper at the quota version (concurrent heartbeats cannot both win).</summary>
    Task<bool> TryClaimQuotaResyncAsync(int serverId, DateTime now, CancellationToken ct = default);

    /// <summary>Gives the claim back after the tasks could not be queued, so a later heartbeat retries.</summary>
    Task ReleaseQuotaResyncAsync(int serverId, CancellationToken ct = default);
    /// <summary>True while a task of one of <paramref name="kinds"/> is pending, assigned or running on the server.</summary>
    Task<bool> HasOpenTaskAsync(int serverId, IReadOnlyCollection<OperationKind> kinds, CancellationToken ct = default);
    Task<List<MailAlias>> GetAliasesAsync(int serverId, CancellationToken ct = default);
    Task<(List<MailAlias> Items, int Total)> GetAliasesPagedAsync(
        int serverId, string? search, int page, int pageSize, string? sortBy, bool sortDescending,
        CancellationToken ct = default, IReadOnlyList<GridFilter>? filters = null);
    Task<MailAlias?> GetAliasAsync(int aliasId, CancellationToken ct = default);
    Task AddAliasAsync(MailAlias alias, CancellationToken ct = default);
    Task UpdateAliasAsync(MailAlias alias, CancellationToken ct = default);
    Task DeleteAliasAsync(MailAlias alias, CancellationToken ct = default);
}
