// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text.Json;
using Aetheus.Back.Components.Logs;
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Mail;

public interface IMailOperationsService
{
    Task<MailCertificateDto> GetCertificateAsync(int serverId, CancellationToken ct = default);
    Task<MailTaskQueuedDto> InstallCertificateAsync(int serverId, string? acmeEmail, CancellationToken ct = default);
    Task<SpamFilterConfigDto> GetSpamFilterAsync(int serverId, CancellationToken ct = default);
    Task<MailTaskQueuedDto> InstallSpamFilterAsync(int serverId, CancellationToken ct = default);
    Task<MailTaskQueuedDto> UpdateSpamFilterAsync(int serverId, UpdateSpamFilterRequest request, CancellationToken ct = default);
    Task<MailTaskQueuedDto> LearnSpamAsync(int serverId, LearnSpamRequest request, CancellationToken ct = default);
    Task<MailTaskQueuedDto> SendTestAsync(int serverId, MailTestDeliveryRequest request, CancellationToken ct = default);
    Task<MailTaskQueuedDto> RefreshQueueAsync(int serverId, CancellationToken ct = default);
    Task<MailTaskQueuedDto> DeleteQueuedMessageAsync(int serverId, string queueId, CancellationToken ct = default);
    Task<MailTaskQueuedDto> RefreshQuotaAsync(int serverId, CancellationToken ct = default);
    Task<int> IngestQuotaReportAsync(int serverId, int taskId, CancellationToken ct = default);

    /// <summary>Recette R2-080: queues the outbound port 25 check of the setup assistant on the server's agent.</summary>
    Task<MailTaskQueuedDto> ProbeOutboundSmtpAsync(int serverId, CancellationToken ct = default);
}

/// <summary>
/// PLAN-005 lots 3, 4 and 6: TLS certificate, rspamd spam filter, delivery test, queue and mailbox usage.
/// Every mutation is a typed operation run by the root-owned mail-manage helper; every endpoint returns
/// the id of the queued task so the client can follow its completion. The quota report is the one task
/// output the backend reads back: only from a successful <c>MailQuotaReport</c> task of the same server.
/// </summary>
public sealed class MailOperationsService(
    IMailRepository mailRepo,
    IMailInventoryRepository inventory,
    IServerRepository serverRepo,
    ITaskService taskService,
    ILogService logService,
    IAuditService audit,
    IEncryptionService encryption,
    TimeProvider timeProvider) : IMailOperationsService
{
    public async Task<MailCertificateDto> GetCertificateAsync(int serverId, CancellationToken ct = default)
    {
        var state = await inventory.GetStateAsync(serverId, ct).ConfigureAwait(false);
        if (state is null) return new MailCertificateDto();
        return new MailCertificateDto
        {
            Hostname = state.Hostname,
            CertPath = state.TlsCertPath,
            IsReadable = state.TlsIsReadable,
            Subject = state.TlsSubject,
            Issuer = state.TlsIssuer,
            ExpiresAt = state.TlsExpiresAt,
            IsSelfSigned = state.TlsIsSelfSigned,
            UsesLetsEncryptLineage = MailValidation.IsValidDomainName(state.Hostname)
                && state.TlsCertPath == LineagePath(state.Hostname)
        };
    }

    public async Task<MailTaskQueuedDto> InstallCertificateAsync(int serverId, string? acmeEmail, CancellationToken ct = default)
    {
        var hostname = await RequireHostnameAsync(serverId, ct).ConfigureAwait(false);
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(acmeEmail))
        {
            if (!MailValidation.IsValidEmail(acmeEmail))
                throw new BadRequestException("Invalid ACME account email.");
            env[MailSetupEnv.CertificateEmail] = acmeEmail;
        }
        var id = await QueueAsync(serverId, $"Mail - TLS certificate ({hostname})", OperationKind.MailInstallCertificate,
            hostname, env, 300, ct).ConfigureAwait(false);
        await audit.LogAsync("MailInstallCertificate", "Mail", serverId, hostname, ct).ConfigureAwait(false);
        return id;
    }

    public async Task<SpamFilterConfigDto> GetSpamFilterAsync(int serverId, CancellationToken ct = default)
    {
        var state = await inventory.GetStateAsync(serverId, ct).ConfigureAwait(false);
        return state is null ? new SpamFilterConfigDto() : new SpamFilterConfigDto
        {
            IsInstalled = state.IsSpamFilterInstalled,
            IsRunning = state.IsSpamFilterRunning,
            Name = state.SpamFilterName,
            Version = state.SpamFilterVersion,
            RejectScore = state.SpamRejectScore,
            AddHeaderScore = state.SpamAddHeaderScore,
            GreylistScore = state.SpamGreylistScore
        };
    }

    public async Task<MailTaskQueuedDto> InstallSpamFilterAsync(int serverId, CancellationToken ct = default)
    {
        var id = await QueueAsync(serverId, "Mail - install spam filter (rspamd)", OperationKind.MailSpamInstall, "-",
            null, 900, ct).ConfigureAwait(false);
        await audit.LogAsync("MailSpamInstall", "Mail", serverId, "rspamd", ct).ConfigureAwait(false);
        return id;
    }

    public async Task<MailTaskQueuedDto> UpdateSpamFilterAsync(int serverId, UpdateSpamFilterRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!MailValidation.AreSpamThresholdsOrdered(request.GreylistScore, request.AddHeaderScore, request.RejectScore))
            throw new BadRequestException("Spam thresholds must satisfy greylist < add header < reject.");
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MailSetupEnv.SpamRejectScore] = Score(request.RejectScore),
            [MailSetupEnv.SpamAddHeaderScore] = Score(request.AddHeaderScore),
            [MailSetupEnv.SpamGreylistScore] = Score(request.GreylistScore)
        };
        var id = await QueueAsync(serverId, "Mail - spam thresholds", OperationKind.MailSpamConfigure, "-", env, 120, ct).ConfigureAwait(false);
        await audit.LogAsync("MailSpamConfigure", "Mail", serverId,
            $"reject={env[MailSetupEnv.SpamRejectScore]} add_header={env[MailSetupEnv.SpamAddHeaderScore]} greylist={env[MailSetupEnv.SpamGreylistScore]}",
            ct).ConfigureAwait(false);
        return id;
    }

    public async Task<MailTaskQueuedDto> LearnSpamAsync(int serverId, LearnSpamRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrEmpty(request.RawMessage) || request.RawMessage.Length > MailValidation.MaxLearnMessageLength)
            throw new BadRequestException("The message must be between 1 byte and 1 MiB.");
        await EnsureMailManageableAsync(serverId, ct).ConfigureAwait(false);
        var kind = request.IsSpam ? "spam" : "ham";
        var env = new Dictionary<string, string>(StringComparer.Ordinal) { [MailSetupEnv.LearnMessage] = request.RawMessage };
        var task = ServerTaskFactory.Operation(serverId, $"Mail - learn {kind}", OperationKind.MailSpamLearn, kind, env, 120);
        // The sample may carry personal data: encrypted at rest like the password-bearing mail tasks.
        task.EnvironmentVariables = TaskEnvProtection.Protect(encryption, JsonSerializer.Serialize(env));
        await TaskQueuePersistence.PersistAndNotifyAsync(mailRepo.AddTaskAsync, taskService, task, ct).ConfigureAwait(false);
        await audit.LogAsync("MailSpamLearn", "Mail", serverId, kind, ct).ConfigureAwait(false);
        return new MailTaskQueuedDto { TaskId = task.Id };
    }

    public async Task<MailTaskQueuedDto> SendTestAsync(int serverId, MailTestDeliveryRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!MailValidation.IsValidEmail(request.From) || !MailValidation.IsValidEmail(request.To))
            throw new BadRequestException("Invalid sender or recipient address.");
        var senderDomain = request.From[(request.From.IndexOf('@') + 1)..];
        var domains = await mailRepo.GetDomainsAsync(serverId, ct).ConfigureAwait(false);
        if (!domains.Any(d => d.Name.Equals(senderDomain, StringComparison.OrdinalIgnoreCase)))
            throw new BadRequestException("The sender must belong to a mail domain of this server (it is DKIM-signed).");
        var env = new Dictionary<string, string>(StringComparer.Ordinal) { [MailSetupEnv.Sender] = request.From };
        var id = await QueueAsync(serverId, $"Mail - delivery test to {request.To}", OperationKind.MailSendTest, request.To,
            env, 150, ct).ConfigureAwait(false);
        await audit.LogAsync("MailSendTest", "Mail", serverId, $"{request.From} -> {request.To}", ct).ConfigureAwait(false);
        return id;
    }

    public Task<MailTaskQueuedDto> RefreshQueueAsync(int serverId, CancellationToken ct = default) =>
        QueueAsync(serverId, "Mail - queue listing", OperationKind.MailViewQueue, "-", null, 60, ct);

    public async Task<MailTaskQueuedDto> DeleteQueuedMessageAsync(int serverId, string queueId, CancellationToken ct = default)
    {
        if (!MailValidation.IsValidQueueId(queueId))
            throw new BadRequestException("Invalid queue id.");
        var id = await QueueAsync(serverId, $"Mail - delete queued message {queueId}", OperationKind.MailQueueDelete,
            queueId, null, 60, ct).ConfigureAwait(false);
        await audit.LogAsync("MailQueueDelete", "Mail", serverId, queueId, ct).ConfigureAwait(false);
        return id;
    }

    public Task<MailTaskQueuedDto> RefreshQuotaAsync(int serverId, CancellationToken ct = default) =>
        QueueAsync(serverId, "Mail - mailbox usage", OperationKind.MailQuotaReport, "-", null, 300, ct);

    public async Task<int> IngestQuotaReportAsync(int serverId, int taskId, CancellationToken ct = default)
    {
        var task = await taskService.GetTaskAsync(taskId, ct).ConfigureAwait(false);
        if (task is null || task.ServerId != serverId)
            throw new NotFoundException($"Task {taskId} not found on this server.");
        var outcome = await inventory.GetTaskOperationAsync(serverId, taskId, ct).ConfigureAwait(false);
        if (outcome != OperationKind.MailQuotaReport)
            throw new BadRequestException("The task is not a mailbox usage report.");
        if (task.Status != TaskExecutionStatus.Success)
            throw new ConflictException("The mailbox usage report has not completed successfully.");

        var logs = await logService.GetTaskLogsAsync(taskId, ct: ct).ConfigureAwait(false);
        var usage = MailTaskOutputParser.ParseQuotaReport(string.Join('\n', logs.Select(l => l.Message)));
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var updated = 0;
        foreach (var account in (await inventory.GetTrackedInventoryAsync(serverId, ct).ConfigureAwait(false)).SelectMany(d => d.Accounts))
        {
            if (!usage.TryGetValue(account.Email, out var kib)) continue;
            account.UsedMb = (int)Math.Min(int.MaxValue, (kib + 1023) / 1024);
            account.UsageMeasuredAt = now;
            updated++;
        }
        if (updated > 0)
            await inventory.SaveChangesAsync(ct).ConfigureAwait(false);
        return updated;
    }

    // Needs no mail grant (the agent opens the socket itself, before any mail stack exists), only an agent
    // that publishes the capability: an older one would leave the task pending until the watchdog.
    public async Task<MailTaskQueuedDto> ProbeOutboundSmtpAsync(int serverId, CancellationToken ct = default)
    {
        var server = await serverRepo.FindServerAsync(serverId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"Server {serverId} not found.");
        if (server.AgentCapabilitiesJson?.Contains($"\"{AgentCapabilities.MailOutboundProbe}\"", StringComparison.Ordinal) != true)
            throw new BadRequestException(
                $"The agent on '{server.Name}' does not publish the '{AgentCapabilities.MailOutboundProbe}' capability: " +
                "update the agent to check outbound port 25.");
        var task = ServerTaskFactory.Operation(serverId, "Mail - outbound port 25 check", OperationKind.MailOutboundProbe,
            MailOutboundProbe.Target, 60);
        await TaskQueuePersistence.PersistAndNotifyAsync(mailRepo.AddTaskAsync, taskService, task, ct).ConfigureAwait(false);
        return new MailTaskQueuedDto { TaskId = task.Id };
    }

    private async Task<MailTaskQueuedDto> QueueAsync(
        int serverId, string name, OperationKind kind, string target,
        IReadOnlyDictionary<string, string>? env, int timeoutSeconds, CancellationToken ct)
    {
        await EnsureMailManageableAsync(serverId, ct).ConfigureAwait(false);
        var task = env is null || env.Count == 0
            ? ServerTaskFactory.Operation(serverId, name, kind, target, timeoutSeconds)
            : ServerTaskFactory.Operation(serverId, name, kind, target, env, timeoutSeconds);
        await TaskQueuePersistence.PersistAndNotifyAsync(mailRepo.AddTaskAsync, taskService, task, ct).ConfigureAwait(false);
        return new MailTaskQueuedDto { TaskId = task.Id };
    }

    private async Task<string> RequireHostnameAsync(int serverId, CancellationToken ct)
    {
        var state = await inventory.GetStateAsync(serverId, ct).ConfigureAwait(false);
        return state is not null && MailValidation.IsValidDomainName(state.Hostname)
            ? state.Hostname
            : throw new BadRequestException("The mail hostname is not known yet: wait for the next agent heartbeat.");
    }

    // Same gate as MailService: without the aetheus-mail sudoers grant the helper's `sudo -n` refuses.
    private async Task EnsureMailManageableAsync(int serverId, CancellationToken ct)
    {
        var server = await serverRepo.FindServerAsync(serverId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"Server {serverId} not found.");
        if (!server.MailSetupAvailable)
            throw new BadRequestException(
                "Mail management is not enabled on this server. Re-run the agent installer with the mail-setup " +
                "capability (--enable-mail-setup) to grant the controlled-sudo helper.");
    }

    private static string LineagePath(string hostname) => $"/etc/letsencrypt/live/{hostname}/fullchain.pem";

    private static string Score(double value) => Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);
}
