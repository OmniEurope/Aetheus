// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Aetheus.Back.Components.Tasks;

namespace Aetheus.Back.Components.Mail;

public interface IMailQuotaResync
{
    /// <summary>Queues one <c>MailSetQuota</c> per account of the server when its helper has just reached the quota
    /// version and the resync has not been done yet. Returns the number of tasks queued.</summary>
    Task<int> ResyncAsync(int serverId, int reportedHelperVersion, CancellationToken ct = default);
}

/// <summary>
/// DEP-005 follow-up: accounts created before the quota plugin have no quota in Dovecot until it is written, so
/// when a server's helper reaches <see cref="MailService.QuotaMinimumHelperVersion"/> each account's stored quota
/// is pushed once through the same <c>mail-manage set-quota</c> task a quota change uses. The "done" mark lives on
/// the server's mail state (see <see cref="IMailRepository.TryClaimQuotaResyncAsync"/>), so a later heartbeat, or a
/// concurrent one, queues nothing; the mark clears while the helper is older, so each upgrade resyncs once. The
/// quota grace stays 0: <c>mail-manage</c> writes no grace and the task carries only the quota in MiB.
/// </summary>
public sealed class MailQuotaResync(IMailRepository repo, ITaskService taskService, TimeProvider time) : IMailQuotaResync
{
    internal const int TimeoutSeconds = 30;

    public async Task<int> ResyncAsync(int serverId, int reportedHelperVersion, CancellationToken ct = default)
    {
        if (reportedHelperVersion < MailService.QuotaMinimumHelperVersion) return 0;
        if (!await repo.TryClaimQuotaResyncAsync(serverId, time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false))
            return 0;
        try
        {
            var accounts = await repo.GetAccountsAsync(serverId, ct).ConfigureAwait(false);
            foreach (var account in accounts)
            {
                var task = ServerTaskFactory.Operation(serverId, $"Mail - set quota {account.Email}",
                    OperationKind.MailSetQuota, target: account.Email,
                    environmentVariables: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [MailSetupEnv.QuotaMb] = account.QuotaMb.ToString(CultureInfo.InvariantCulture)
                    },
                    timeoutSeconds: TimeoutSeconds);
                await TaskQueuePersistence.PersistAndNotifyAsync(repo.AddTaskAsync, taskService, task, ct).ConfigureAwait(false);
            }
            return accounts.Count;
        }
        catch
        {
            // Nothing is lost: give the claim back so the next heartbeat retries (a task already queued is
            // an idempotent set-quota with the same value).
            await repo.ReleaseQuotaResyncAsync(serverId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
}
