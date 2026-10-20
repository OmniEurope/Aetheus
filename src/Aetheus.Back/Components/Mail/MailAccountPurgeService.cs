// SPDX-License-Identifier: EUPL-1.2
using Microsoft.Extensions.Caching.Memory;

namespace Aetheus.Back.Components.Mail;

public interface IMailAccountPurgeService
{
    /// <summary>Deletes the server's Aetheus mailbox rows the server no longer reports.</summary>
    Task<MailAccountPurgeResultDto> PurgeMissingAsync(int serverId, CancellationToken ct = default);
}

/// <summary>
/// Recette R3-49 (decision of 2026-10-12, option a): "Purger" deletes the Aetheus rows of the mailboxes the
/// heartbeat reconciliation marked absent from the server (<c>MissingSince</c> set). The reconciliation only
/// deactivates such rows (PLAN-005), so after a reinstall the old mailboxes stayed listed. Nothing is queued
/// on the server: those mailboxes are already gone there. A row deactivated by hand but still on the server
/// (<c>IsActive = false</c>, <c>MissingSince</c> empty) is kept. Audited with the count and the addresses.
/// </summary>
public sealed class MailAccountPurgeService(
    IMailRepository mailRepo,
    IAuditService audit,
    IMemoryCache cache) : IMailAccountPurgeService
{
    private const int MaxAuditDetailsLength = 2000;

    public async Task<MailAccountPurgeResultDto> PurgeMissingAsync(int serverId, CancellationToken ct = default)
    {
        var missing = await mailRepo.GetMissingAccountsAsync(serverId, ct).ConfigureAwait(false);
        if (missing.Count == 0) return new MailAccountPurgeResultDto { Removed = 0 };

        await mailRepo.DeleteAccountsAsync(missing, ct).ConfigureAwait(false);
        MailCacheKeys.Invalidate(cache, serverId);

        var details = $"{missing.Count}: {string.Join(", ", missing.Select(account => account.Email))}";
        if (details.Length > MaxAuditDetailsLength) details = details[..MaxAuditDetailsLength];
        await audit.LogAsync("MailAccountsPurged", "Mail", serverId, details, ct).ConfigureAwait(false);
        return new MailAccountPurgeResultDto { Removed = missing.Count };
    }
}
