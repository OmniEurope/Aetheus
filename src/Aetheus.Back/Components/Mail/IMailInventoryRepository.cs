// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Mail;

/// <summary>Provides tracked mail inventory access with batched writes and one SaveChanges per
/// reconciliation, quota-ingestion or DNS-verification pass.</summary>
public interface IMailInventoryRepository
{
    /// <summary>Tracked domains of the server with their accounts and aliases.</summary>
    Task<List<MailDomain>> GetTrackedInventoryAsync(int serverId, CancellationToken ct = default);

    /// <summary>Addresses among <paramref name="emails"/> already used by an account of another server
    /// (the account email index is unique across the whole fleet).</summary>
    Task<HashSet<string>> GetAccountEmailsOnOtherServersAsync(
        int serverId, IReadOnlyCollection<string> emails, CancellationToken ct = default);

    /// <summary>Targets of removal operations still pending, or queued since <paramref name="since"/>:
    /// the inventory of a heartbeat may predate them and must not resurrect the removed rows.</summary>
    Task<HashSet<(OperationKind Kind, string Target)>> GetRecentRemovalTargetsAsync(
        int serverId, DateTime since, CancellationToken ct = default);

    Task<MailState?> GetStateAsync(int serverId, CancellationToken ct = default);

    Task<MailDomain?> GetTrackedDomainAsync(int serverId, int domainId, CancellationToken ct = default);

    /// <summary>Tracked domains of every server, for the periodic DNS verification.</summary>
    Task<List<MailDomain>> GetTrackedDomainsDueForDnsCheckAsync(DateTime checkedBefore, int take, CancellationToken ct = default);

    /// <summary>Operation kind of a task of this server, or null when the task is unknown or belongs elsewhere.</summary>
    Task<OperationKind?> GetTaskOperationAsync(int serverId, int taskId, CancellationToken ct = default);

    void AddDomain(MailDomain domain);

    /// <summary>Removes a tracked domain; its accounts and aliases go with it (cascade).</summary>
    void RemoveDomain(MailDomain domain);

    Task SaveChangesAsync(CancellationToken ct = default);
}
