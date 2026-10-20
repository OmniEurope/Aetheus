// SPDX-License-Identifier: EUPL-1.2
using System.Collections.Concurrent;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Mail;

/// <summary>Counts of one reconciliation pass; <see cref="HasChanges"/> drives cache invalidation.</summary>
public sealed record MailReconciliationResult(int Created, int MarkedMissing, int Restored, int Updated, int Skipped)
{
    public static MailReconciliationResult Empty { get; } = new(0, 0, 0, 0, 0);
    public bool HasChanges => Created + MarkedMissing + Restored + Updated > 0;
}

public interface IMailStateReconciler
{
    Task<MailReconciliationResult> ReconcileAsync(int serverId, MailDataDto mail, CancellationToken ct = default);
}

/// <summary>Adopt reported rows, deactivate missing rows without deleting them, and reactivate rows
/// that return. Ignore incomplete inventory, protect recently created rows during <see
/// cref="Grace"/>, and do not resurrect pending removals.</summary>
public sealed class MailStateReconciler(
    IMailInventoryRepository repo,
    TimeProvider timeProvider,
    ILogger<MailStateReconciler> logger) : IMailStateReconciler
{
    internal static readonly TimeSpan Grace = TimeSpan.FromMinutes(10);

    // One pass per server at a time inside this process: two heartbeats reconciled concurrently would
    // both see an address as new and race on the unique indexes.
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> s_serverGates = new();

    public async Task<MailReconciliationResult> ReconcileAsync(int serverId, MailDataDto mail, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mail);
        if (!mail.IsInstalled) return MailReconciliationResult.Empty;

        var gate = s_serverGates.GetOrAdd(serverId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ReconcileCoreAsync(serverId, mail, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<MailReconciliationResult> ReconcileCoreAsync(int serverId, MailDataDto mail, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var pass = new Pass(now, await repo.GetRecentRemovalTargetsAsync(serverId, now - Grace, ct).ConfigureAwait(false));
        // "Réinstaller la messagerie": the purge dropped the rows it is erasing on the host. A heartbeat taken
        // while it runs (or just after) still lists them and would adopt them back.
        if (pass.IsBeingRemoved(OperationKind.MailPurge, "-"))
            return MailReconciliationResult.Empty;
        var domains = await repo.GetTrackedInventoryAsync(serverId, ct).ConfigureAwait(false);

        if (mail.DomainsCollected)
            ReconcileDomains(serverId, mail, domains, pass);
        ApplyDkimKeys(mail.DkimKeys, domains, pass);
        if (mail.AccountsCollected)
            await ReconcileAccountsAsync(serverId, mail.Accounts, domains, pass, ct).ConfigureAwait(false);
        if (mail.AliasesCollected)
            ReconcileAliases(mail.Aliases, domains, pass);

        var result = new MailReconciliationResult(pass.Created, pass.MarkedMissing, pass.Restored, pass.Updated, pass.Skipped);
        if (!result.HasChanges)
        {
            // Skips repeat on every heartbeat until the operator fixes their cause: keep them out of Information.
            if (result.Skipped > 0)
                logger.LogDebug("Mail reconciliation for server {ServerId}: {Skipped} reported rows skipped", serverId, result.Skipped);
            return result;
        }

        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        logger.LogInformation(
            "Mail reconciliation for server {ServerId}: {Created} created, {Missing} marked missing, {Restored} restored, {Updated} updated, {Skipped} skipped",
            serverId, result.Created, result.MarkedMissing, result.Restored, result.Updated, result.Skipped);
        return result;
    }

    private void ReconcileDomains(int serverId, MailDataDto mail, List<MailDomain> domains, Pass pass)
    {
        var reported = mail.Domains
            .Select(d => d.Name.Trim())
            .Where(MailValidation.IsValidDomainName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var name in reported.Where(n => !domains.Any(d => Same(d.Name, n))))
        {
            if (pass.IsBeingRemoved(OperationKind.MailRemoveDomain, name)) { pass.Skipped++; continue; }
            var key = mail.DkimKeys.FirstOrDefault(k => Same(k.Domain, name));
            var domain = new MailDomain
            {
                ServerId = serverId,
                Name = name.ToLowerInvariant(),
                IsActive = true,
                Source = MailRecordSource.Adopted,
                DkimSelector = key?.Selector ?? "default",
                DkimPublicKey = key?.PublicKey ?? string.Empty,
                CreatedAt = pass.Now
            };
            repo.AddDomain(domain);
            domains.Add(domain);
            pass.Created++;
        }

        foreach (var domain in domains)
            pass.ApplyPresence(domain, reported.Contains(domain.Name),
                d => d.MissingSince, (d, v) => d.MissingSince = v, (d, v) => d.IsActive = v, d => d.CreatedAt);
    }

    private static void ApplyDkimKeys(List<MailDkimKeyDto> keys, List<MailDomain> domains, Pass pass)
    {
        foreach (var key in keys)
        {
            var domain = domains.FirstOrDefault(d => Same(d.Name, key.Domain));
            if (domain is null || !MailValidation.IsValidDkimSelector(key.Selector)) continue;
            var changed = false;
            if (domain.DkimSelector != key.Selector) { domain.DkimSelector = key.Selector; changed = true; }
            if (key.PublicKey.Length > 0 && domain.DkimPublicKey != key.PublicKey) { domain.DkimPublicKey = key.PublicKey; changed = true; }
            if (changed && domain.Id != 0) pass.Updated++;
        }
    }

    private async Task ReconcileAccountsAsync(
        int serverId, List<MailAccountDto> reportedAccounts, List<MailDomain> domains, Pass pass, CancellationToken ct)
    {
        var reported = reportedAccounts
            .Where(a => MailValidation.IsValidEmail(a.Email))
            .GroupBy(a => a.Email, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var known = domains.SelectMany(d => d.Accounts).ToList();
        var knownEmails = known.Select(a => a.Email).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = reported.Keys.Where(email => !knownEmails.Contains(email)).ToList();
        var elsewhere = await repo.GetAccountEmailsOnOtherServersAsync(serverId, candidates, ct).ConfigureAwait(false);

        foreach (var email in candidates)
        {
            var domain = domains.FirstOrDefault(d => Same(d.Name, DomainOf(email)));
            if (domain is null || elsewhere.Contains(email) || pass.IsBeingRemoved(OperationKind.MailDeleteAccount, email))
            {
                pass.Skipped++;
                continue;
            }
            // Quota 0 = not managed by Aetheus: the adopted stack enforces its own (or no) quota.
            domain.Accounts.Add(new MailAccount
            {
                Email = email.ToLowerInvariant(),
                QuotaMb = 0,
                IsActive = true,
                Source = MailRecordSource.Adopted,
                CreatedAt = pass.Now
            });
            pass.Created++;
        }

        foreach (var account in known)
            pass.ApplyPresence(account, reported.ContainsKey(account.Email),
                a => a.MissingSince, (a, v) => a.MissingSince = v, (a, v) => a.IsActive = v, a => a.CreatedAt);
    }

    private static void ReconcileAliases(List<MailAliasDto> reportedAliases, List<MailDomain> domains, Pass pass)
    {
        var reported = reportedAliases
            .Where(a => MailValidation.IsValidEmail(a.SourceEmail) && MailValidation.IsValidEmail(a.DestinationEmail))
            .GroupBy(a => a.SourceEmail, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().DestinationEmail, StringComparer.OrdinalIgnoreCase);
        var known = domains.SelectMany(d => d.Aliases).ToList();

        foreach (var (source, destination) in reported)
        {
            var existing = known.FirstOrDefault(a => Same(a.SourceEmail, source));
            if (existing is not null)
            {
                if (!Same(existing.DestinationEmail, destination))
                {
                    existing.DestinationEmail = destination;
                    pass.Updated++;
                }
                continue;
            }
            var domain = domains.FirstOrDefault(d => Same(d.Name, DomainOf(source)));
            if (domain is null || pass.IsBeingRemoved(OperationKind.MailRemoveAlias, source))
            {
                pass.Skipped++;
                continue;
            }
            domain.Aliases.Add(new MailAlias
            {
                SourceEmail = source.ToLowerInvariant(),
                DestinationEmail = destination,
                IsActive = true,
                Source = MailRecordSource.Adopted,
                CreatedAt = pass.Now
            });
            pass.Created++;
        }

        foreach (var alias in known)
            pass.ApplyPresence(alias, reported.ContainsKey(alias.SourceEmail),
                a => a.MissingSince, (a, v) => a.MissingSince = v, (a, v) => a.IsActive = v, a => a.CreatedAt);
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string DomainOf(string email) => email[(email.IndexOf('@') + 1)..];

    private sealed class Pass(DateTime now, HashSet<(OperationKind Kind, string Target)> removals)
    {
        public DateTime Now { get; } = now;
        public int Created { get; set; }
        public int MarkedMissing { get; set; }
        public int Restored { get; set; }
        public int Updated { get; set; }
        public int Skipped { get; set; }

        public bool IsBeingRemoved(OperationKind kind, string target) => removals.Contains((kind, target.ToLowerInvariant()));

        // Present rows the reconciliation had deactivated come back; a row the operator deactivated
        // (MissingSince == null) is never reactivated. Absent rows older than the grace are deactivated once.
        public void ApplyPresence<T>(T row, bool present, Func<T, DateTime?> getMissing, Action<T, DateTime?> setMissing,
            Action<T, bool> setActive, Func<T, DateTime> getCreated)
        {
            var missingSince = getMissing(row);
            if (present)
            {
                if (missingSince is null) return;
                setMissing(row, null);
                setActive(row, true);
                Restored++;
                return;
            }
            if (missingSince is not null || getCreated(row) > Now - Grace) return;
            setMissing(row, Now);
            setActive(row, false);
            MarkedMissing++;
        }
    }
}
