// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Components.Tasks;

namespace Aetheus.Back.Components.Mail;

public interface IMailMasterAccessService
{
    /// <summary>Generates a new master password, queues <c>mail-manage master-set</c> and stores the password.</summary>
    Task<MailTaskQueuedDto> PrepareAsync(int serverId, CancellationToken ct = default);
}

/// <summary>
/// Recette R1-41 / R3-50: "Préparer l'accès administrateur" for a server whose mail stack was set up before
/// the master user existed (a new setup or reinstall creates it with the stack, see <see cref="MailService"/>).
/// Modelled on the no-reply mailbox of R1-16: the password is generated here, rides to the helper encrypted
/// in the task and over stdin, and is stored encrypted once the task is queued, so a refused request keeps
/// the previous secret. Administrators only (the controller); audited without the password.
/// </summary>
public sealed class MailMasterAccessService(
    IMailRepository mailRepo,
    IMailInventoryRepository inventory,
    IServerRepository serverRepo,
    ITaskService taskService,
    IEncryptionService encryption,
    IMailMasterSecrets secrets,
    IAuditService audit) : IMailMasterAccessService
{
    internal const int TimeoutSeconds = 60;

    public async Task<MailTaskQueuedDto> PrepareAsync(int serverId, CancellationToken ct = default)
    {
        var server = await serverRepo.FindServerAsync(serverId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"Server {serverId} not found.");
        if (!server.MailSetupAvailable)
            throw new BadRequestException(
                "Mail management is not enabled on this server. Re-run the agent installer with the mail-setup " +
                "capability (--enable-mail-setup) to grant the controlled-sudo helper.");
        var state = await inventory.GetStateAsync(serverId, ct).ConfigureAwait(false);
        if (state is null || state.HelperVersion < MailMasterAccess.MinimumHelperVersion)
            throw new BadRequestException(
                $"The mail helper of this server cannot create the administrator access (version {state?.HelperVersion ?? 0}, " +
                $"{MailMasterAccess.MinimumHelperVersion} required). Re-run the agent installer with --enable-mail-setup.");
        if (await mailRepo.HasOpenTaskAsync(serverId, MailStackPurgeService.StackRebuildKinds, ct).ConfigureAwait(false))
            throw new ConflictException("A mail setup or reinstall is running on this server: it creates the administrator access itself.");

        var password = MailMasterAccess.NewPassword();
        var env = new Dictionary<string, string>(StringComparer.Ordinal) { [MailSetupEnv.MasterPassword] = password };
        var task = ServerTaskFactory.Operation(serverId, "Mail - prepare the administrator access",
            OperationKind.MailMasterAccess, "-", env, timeoutSeconds: TimeoutSeconds);
        // The password rides encrypted at rest, like every secret-bearing mail task (F-001 / F11b).
        task.EnvironmentVariables = TaskEnvProtection.Protect(encryption, JsonSerializer.Serialize(env));
        await TaskQueuePersistence.PersistAndNotifyAsync(mailRepo.AddTaskAsync, taskService, task, ct).ConfigureAwait(false);

        await secrets.StoreAsync(serverId, password, ct).ConfigureAwait(false);
        await audit.LogAsync("MailMasterAccessPrepared", "Mail", serverId, MailMasterAccess.MasterUser, ct).ConfigureAwait(false);
        return new MailTaskQueuedDto { TaskId = task.Id };
    }
}
