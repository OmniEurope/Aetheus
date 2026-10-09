// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Components.Tasks;
using Microsoft.Extensions.Caching.Memory;

namespace Aetheus.Back.Components.Mail;

public interface IMailStackPurgeService
{
    Task<MailTaskQueuedDto> PurgeAsync(int serverId, MailPurgeRequest request, CancellationToken ct = default);
}

/// <summary>
/// "Réinstaller la messagerie" (recette R2-083 / R2-088, decision of 2026-10-03: erase everything). Queues
/// <c>mail-manage purge</c>, which removes the services, packages, configuration, mailboxes and DKIM keys of
/// the host, and drops the Aetheus rows that mirror them (domains, with their accounts and aliases), so the
/// page returns to "not configured" and the same domain can be set up again. While the purge runs the
/// heartbeat reconciliation leaves the rows alone (<see cref="MailStateReconciler"/>); a purge that fails
/// lets the next heartbeats adopt back whatever is still on the host.
/// </summary>
public sealed class MailStackPurgeService(
    IMailRepository mailRepo,
    IMailInventoryRepository inventory,
    IServerRepository serverRepo,
    ITaskService taskService,
    IAuditService audit,
    IMemoryCache cache) : IMailStackPurgeService
{
    /// <summary>The first mail-manage helper version that has the <c>purge</c> sub-command.</summary>
    internal const int MinimumHelperVersion = 3;

    // apt-get purge plus autoremove: as generous as the setup that installed the stack.
    internal const int TimeoutSeconds = 900;

    /// <summary>A setup and a purge never run side by side on one server (R2-085: two setups 3 s apart).</summary>
    internal static readonly OperationKind[] StackRebuildKinds = [OperationKind.MailSetup, OperationKind.MailPurge];

    public async Task<MailTaskQueuedDto> PurgeAsync(int serverId, MailPurgeRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var server = await serverRepo.FindServerAsync(serverId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"Server {serverId} not found.");
        if (!server.MailSetupAvailable)
            throw new BadRequestException(
                "Mail management is not enabled on this server. Re-run the agent installer with the mail-setup " +
                "capability (--enable-mail-setup) to grant the controlled-sudo helper.");

        var state = await inventory.GetStateAsync(serverId, ct).ConfigureAwait(false);
        if (state is null || state.HelperVersion < MinimumHelperVersion)
            throw new BadRequestException(
                $"The mail helper of this server cannot erase the stack (version {state?.HelperVersion ?? 0}, " +
                $"{MinimumHelperVersion} required). Re-run the agent installer with --enable-mail-setup.");

        var domains = await inventory.GetTrackedInventoryAsync(serverId, ct).ConfigureAwait(false);
        var confirmation = request.Confirmation.Trim();
        if (!ConfirmationWords(domains.Select(d => d.Name), state.Hostname)
                .Contains(confirmation, StringComparer.OrdinalIgnoreCase))
            throw new BadRequestException("Type the mail domain of this server (or its mail host name) to confirm.");

        if (await mailRepo.HasOpenTaskAsync(serverId, StackRebuildKinds, ct).ConfigureAwait(false))
            throw new ConflictException("A mail setup or reinstall is already running on this server.");

        var task = ServerTaskFactory.Operation(serverId, "Mail - erase the mail stack (reinstall)",
            OperationKind.MailPurge, "-", TimeoutSeconds);
        await TaskQueuePersistence.PersistAndNotifyAsync(mailRepo.AddTaskAsync, taskService, task, ct).ConfigureAwait(false);

        foreach (var domain in domains)
            inventory.RemoveDomain(domain);
        await inventory.SaveChangesAsync(ct).ConfigureAwait(false);
        MailCacheKeys.Invalidate(cache, serverId);

        await audit.LogAsync("MailPurge", "Mail", serverId, confirmation, ct).ConfigureAwait(false);
        return new MailTaskQueuedDto { TaskId = task.Id };
    }

    /// <summary>The words a confirmation may type: a mail domain of the server or its mail host name (the
    /// page asks for the domain, and for the host name only when it knows no domain).</summary>
    internal static IReadOnlyList<string> ConfirmationWords(IEnumerable<string> domains, string hostname) =>
        [.. domains.Append(hostname).Where(MailValidation.IsValidDomainName)];
}
