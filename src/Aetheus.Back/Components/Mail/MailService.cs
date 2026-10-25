// SPDX-License-Identifier: EUPL-1.2
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Aetheus.Back.Components.Mail.Mailbox;
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data.Entities;
using Npgsql;

namespace Aetheus.Back.Components.Mail;

public class MailService(
    IMailRepository repo, IAuditService audit, IEncryptionService encryption, IServerRepository serverRepo, ITaskService taskService,
    IMailMasterSecrets masterSecrets, IMailSenderSecret? senderSecret = null) : IMailService
{
    // Dispatch typed operations through the root-owned mail-manage helper for the non-root agent.
    private static readonly Dictionary<MailAction, (OperationKind Kind, string Target)> s_actions = new()
    {
        [MailAction.StartPostfix] = (OperationKind.MailStartPostfix, "-"),
        [MailAction.StopPostfix] = (OperationKind.MailStopPostfix, "-"),
        [MailAction.RestartPostfix] = (OperationKind.MailRestartPostfix, "-"),
        [MailAction.ReloadPostfix] = (OperationKind.MailReloadPostfix, "-"),
        [MailAction.StartDovecot] = (OperationKind.MailStartDovecot, "-"),
        [MailAction.StopDovecot] = (OperationKind.MailStopDovecot, "-"),
        [MailAction.RestartDovecot] = (OperationKind.MailRestartDovecot, "-"),
        [MailAction.ReloadDovecot] = (OperationKind.MailReloadDovecot, "-"),
        [MailAction.FlushQueue] = (OperationKind.MailFlushQueue, "-"),
        [MailAction.ViewQueue] = (OperationKind.MailViewQueue, "-"),
        [MailAction.TestConfig] = (OperationKind.MailTestConfig, "-"),
        [MailAction.StartSpamFilter] = (OperationKind.MailServiceControl, "rspamd:start"),
        [MailAction.StopSpamFilter] = (OperationKind.MailServiceControl, "rspamd:stop"),
        [MailAction.RestartSpamFilter] = (OperationKind.MailServiceControl, "rspamd:restart"),
        [MailAction.RestartOpenDkim] = (OperationKind.MailServiceControl, "opendkim:restart"),
        [MailAction.ApplyRetryTuning] = (OperationKind.MailRetryTuning, "-")
    };

    private const string SetupAlreadyRunning = "A mail setup or reinstall is already running on this server.";

    // One setup recorded and queued at a time per server inside this process.
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> s_setupGates = new();

    private Task QueueTaskAsync(ServerTask task, CancellationToken ct = default)
        => TaskQueuePersistence.PersistAndNotifyAsync(repo.AddTaskAsync, taskService, task, ct);

    private async Task<MailAccount> GetAccountForServerAsync(
        int serverId, int accountId, CancellationToken ct)
    {
        var account = await repo.GetAccountAsync(accountId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"Mail account {accountId} not found.");
        return account.MailDomain.ServerId == serverId
            ? account
            : throw new NotFoundException($"Mail account {accountId} not found on this server.");
    }

    private async Task<MailAlias> GetAliasForServerAsync(
        int serverId, int aliasId, CancellationToken ct)
    {
        var alias = await repo.GetAliasAsync(aliasId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"Mail alias {aliasId} not found.");
        return alias.MailDomain.ServerId == serverId
            ? alias
            : throw new NotFoundException($"Mail alias {aliasId} not found.");
    }

    public async Task<MailDataDto> GetStateAsync(int serverId, CancellationToken ct = default)
    {
        var state = await repo.GetStateAsync(serverId, ct).ConfigureAwait(false);
        return MailStateProjector.ToDto(state);
    }

    public async Task<List<MailDomainDto>> GetDomainsAsync(int serverId, CancellationToken ct = default)
    {
        var domains = await repo.GetDomainsAsync(serverId, ct).ConfigureAwait(false);
        return domains.Select(MapDomain).ToList();
    }

    public async Task<PaginatedResult<MailDomainDto>> GetDomainsAsync(
        int serverId, PaginationRequest request, CancellationToken ct = default)
    {
        var (page, pageSize) = request.Normalize();
        var (items, total) = await repo.GetDomainsPagedAsync(
            serverId, request.Search, page, pageSize, request.SortBy, request.SortDescending, ct, request.Filters).ConfigureAwait(false);
        return Page(items.Select(MapDomain), total, page, pageSize);
    }

    public async Task<MailDomainDto> GetDomainAsync(int serverId, int domainId, CancellationToken ct = default)
    {
        var domain = await repo.GetDomainAsync(serverId, domainId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"Mail domain {domainId} not found.");
        return MapDomain(domain);
    }

    public async Task<MailDomainDto> CreateDomainAsync(int serverId, CreateMailDomainRequest request, CancellationToken ct = default)
    {
        if (!MailCommandHelper.IsValidDomainName(request.Name))
            throw new BadRequestException("Invalid domain name.");

        var domain = new MailDomain
        {
            ServerId = serverId,
            Name = request.Name,
            DkimSelector = request.DkimSelector
        };

        await EnsureMailManageableAsync(serverId, ct).ConfigureAwait(false);
        await repo.AddDomainAsync(domain, ct).ConfigureAwait(false);

        // S-FEAT-W8KN: dispatch the typed MailAddDomain operation via the root-owned mail-manage helper
        // (the old free-form postconf/systemctl shell task always failed for the non-root agent). The
        // new domain is the operation target; no secret is involved. Queued via QueueTaskAsync so the
        // TaskQueued SignalR event is broadcast (top-bar tracker).
        await QueueTaskAsync(ServerTaskFactory.Operation(serverId, $"Mail - add domain {request.Name}",
            OperationKind.MailAddDomain, request.Name,
            new Dictionary<string, string> { [MailSetupEnv.DkimSelector] = request.DkimSelector }, timeoutSeconds: 60), ct).ConfigureAwait(false);

        await audit.LogAsync("MailAddDomain", "Mail", serverId, request.Name, ct).ConfigureAwait(false);
        return MapDomain(domain);
    }

    public async Task<MailDomainDto> UpdateDomainAsync(int serverId, int domainId, UpdateMailDomainRequest request, CancellationToken ct = default)
    {
        var domain = await repo.GetDomainAsync(serverId, domainId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"Mail domain {domainId} not found.");

        if (request.IsActive.HasValue)
            domain.IsActive = request.IsActive.Value;
        if (request.DkimSelector is not null)
        {
            if (!MailCommandHelper.IsValidDkimSelector(request.DkimSelector))
                throw new BadRequestException("Invalid DKIM selector.");
            domain.DkimSelector = request.DkimSelector;
        }

        await repo.UpdateDomainAsync(domain, ct).ConfigureAwait(false);
        await audit.LogAsync("MailUpdateDomain", "Mail", serverId, domain.Name, ct).ConfigureAwait(false);
        return MapDomain(domain);
    }

    public async Task DeleteDomainAsync(int serverId, int domainId, CancellationToken ct = default)
    {
        var domain = await repo.GetDomainAsync(serverId, domainId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"Mail domain {domainId} not found.");

        await repo.DeleteDomainAsync(domain, ct).ConfigureAwait(false);

        // Typed op via the root-owned mail-manage helper (replaces the dead `postconf $(...) &&` shell).
        await QueueTaskAsync(ServerTaskFactory.Operation(serverId, $"Mail - remove domain {domain.Name}",
            OperationKind.MailRemoveDomain, target: domain.Name, timeoutSeconds: 30), ct).ConfigureAwait(false);

        await audit.LogAsync("MailRemoveDomain", "Mail", serverId, domain.Name, ct).ConfigureAwait(false);
    }

    public async Task<List<MailAccountDto>> GetAccountsAsync(int serverId, CancellationToken ct = default)
    {
        var accounts = await repo.GetAccountsAsync(serverId, ct).ConfigureAwait(false);
        return accounts.Select(MapAccount).ToList();
    }

    public async Task<PaginatedResult<MailAccountDto>> GetAccountsAsync(
        int serverId, PaginationRequest request, CancellationToken ct = default)
    {
        var (page, pageSize) = request.Normalize();
        var (items, total) = await repo.GetAccountsPagedAsync(
            serverId, request.Search, page, pageSize, request.SortBy, request.SortDescending, ct, request.Filters).ConfigureAwait(false);
        return Page(items.Select(MapAccount), total, page, pageSize);
    }

    public async Task<MailAccountDto> CreateAccountAsync(int serverId, CreateMailAccountRequest request, CancellationToken ct = default)
    {
        if (!MailCommandHelper.IsValidEmail(request.Email))
            throw new BadRequestException("Invalid email address.");
        if (!MailCommandHelper.IsValidPassword(request.Password))
            throw new BadRequestException("Password must be 12 to 100 characters and contain no control characters.");

        // Find the domain for this email
        var emailDomain = request.Email.Split('@')[1];
        var domains = await repo.GetDomainsAsync(serverId, ct).ConfigureAwait(false);
        var domain = domains.FirstOrDefault(d => d.Name.Equals(emailDomain, StringComparison.OrdinalIgnoreCase))
            ?? throw new BadRequestException($"Domain '{emailDomain}' is not configured on this server.");

        await EnsureMailManageableAsync(serverId, ct).ConfigureAwait(false);
        // A limited mailbox needs the helper that writes the quota; an unlimited one (0) works with any helper.
        if (request.QuotaMb > 0)
            await EnsureQuotaHelperAsync(serverId, ct).ConfigureAwait(false);

        var account = await ClaimAccountRowAsync(serverId, domain, request, ct).ConfigureAwait(false);

        // S-FEAT-W8KN: typed MailAddAccount via the mail-manage helper. The email is the target; the
        // parent domain and quota ride in env vars and the password is piped to the helper over stdin
        // (never argv). The password-bearing env JSON is encrypted at rest like MailSetup.
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MailSetupEnv.Domain] = emailDomain,
            [MailSetupEnv.QuotaMb] = request.QuotaMb.ToString(CultureInfo.InvariantCulture),
            [MailSetupEnv.AccountPassword] = request.Password
        };
        var task = ServerTaskFactory.Operation(serverId, $"Mail - add account {request.Email}",
            OperationKind.MailAddAccount, request.Email, env, timeoutSeconds: 30);
        task.EnvironmentVariables = TaskEnvProtection.Protect(encryption, JsonSerializer.Serialize(env));
        await QueueTaskAsync(task, ct).ConfigureAwait(false);
        await StoreSenderPasswordAsync(request.Email, request.Password, ct).ConfigureAwait(false);

        await audit.LogAsync("MailAddAccount", "Mail", serverId, request.Email, ct).ConfigureAwait(false);
        return MapAccount(account);
    }

    /// <summary>When the mailbox is the default sender's (no-reply), the password just queued for the server is also
    /// the one the sender logs in with: both are written once the task is queued, so a refused request keeps both
    /// old values.</summary>
    private Task StoreSenderPasswordAsync(string email, string password, CancellationToken ct) =>
        senderSecret?.StoreIfSenderAsync(email, password, ct) ?? Task.CompletedTask;

    /// <summary>The Aetheus row the new mailbox is recorded on. The address is unique across servers, so a
    /// row already holding it is either reused or refused (409), never inserted twice. A row the heartbeat
    /// reconciliation flagged absent from the server (<c>MissingSince</c> set) records a mailbox that is gone:
    /// creating it again is the way back, so the same row is reused and treated like a new one (active, not
    /// missing, new quota); the reconciliation then keeps or flags it again from what the server reports. Any
    /// other existing row is a mailbox that is (or was deliberately kept) on a server: it is updated, not
    /// created.</summary>
    private async Task<MailAccount> ClaimAccountRowAsync(
        int serverId, MailDomain domain, CreateMailAccountRequest request, CancellationToken ct)
    {
        var existing = await repo.FindAccountByEmailAsync(request.Email, ct).ConfigureAwait(false);
        if (existing is null)
        {
            var created = new MailAccount
            {
                MailDomainId = domain.Id,
                Email = request.Email,
                QuotaMb = request.QuotaMb,
                IsActive = true
            };
            await repo.AddAccountAsync(created, ct).ConfigureAwait(false);
            return created;
        }

        if (existing.MailDomain.ServerId != serverId)
            throw new ConflictException($"The address {request.Email} already belongs to a mail domain on another server.");
        if (existing.MissingSince is null)
            throw new ConflictException($"The mailbox {request.Email} already exists on this server: update it instead of creating it.");

        existing.MailDomainId = domain.Id;
        existing.QuotaMb = request.QuotaMb;
        existing.IsActive = true;
        existing.MissingSince = null;
        await repo.UpdateAccountAsync(existing, ct).ConfigureAwait(false);
        return existing;
    }

    public async Task<MailAccountDto> UpdateAccountAsync(int serverId, int accountId, UpdateMailAccountRequest request, CancellationToken ct = default)
    {
        var account = await GetAccountForServerAsync(serverId, accountId, ct).ConfigureAwait(false);

        if (request.QuotaMb is { } quotaMb && quotaMb != account.QuotaMb)
        {
            if (!MailValidation.IsValidQuotaMb(quotaMb))
                throw new BadRequestException("Invalid mailbox quota.");

            await EnsureMailManageableAsync(serverId, ct).ConfigureAwait(false);
            await EnsureQuotaHelperAsync(serverId, ct).ConfigureAwait(false);

            // DEP-005: the stored quota is only a number until Dovecot knows it. mail-manage set-quota
            // writes it on the account's users-file line, where the quota plugin refuses an IMAP APPEND
            // or an LMTP delivery that would exceed it.
            account.QuotaMb = quotaMb;
            await QueueTaskAsync(ServerTaskFactory.Operation(serverId, $"Mail - set quota {account.Email}",
                OperationKind.MailSetQuota, target: account.Email,
                environmentVariables: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [MailSetupEnv.QuotaMb] = quotaMb.ToString(CultureInfo.InvariantCulture)
                },
                timeoutSeconds: 30), ct).ConfigureAwait(false);
        }
        if (request.IsActive.HasValue)
            account.IsActive = request.IsActive.Value;
        if (!string.IsNullOrWhiteSpace(request.NewPassword))
        {
            if (!MailCommandHelper.IsValidPassword(request.NewPassword))
                throw new BadRequestException("Password must be 12 to 100 characters and contain no control characters.");

            await EnsureMailManageableAsync(serverId, ct).ConfigureAwait(false);

            // S-TECH-MCPW: typed MailChangePassword via the mail-manage helper. The email is the target;
            // the new password rides in an encrypted env var and is piped to the helper over stdin (never
            // argv), mirroring MailAddAccount. Replaces the old free-form `doveadm pw -p` shell task that
            // put the cleartext password on the process argv.
            var pwdEnv = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [MailSetupEnv.AccountPassword] = request.NewPassword
            };
            var pwdTask = ServerTaskFactory.Operation(serverId, $"Mail - change password {account.Email}",
                OperationKind.MailChangePassword, account.Email, pwdEnv, timeoutSeconds: 15);
            pwdTask.EnvironmentVariables = TaskEnvProtection.Protect(encryption, JsonSerializer.Serialize(pwdEnv));
            await QueueTaskAsync(pwdTask, ct).ConfigureAwait(false);
            await StoreSenderPasswordAsync(account.Email, request.NewPassword, ct).ConfigureAwait(false);
        }

        await repo.UpdateAccountAsync(account, ct).ConfigureAwait(false);
        await audit.LogAsync("MailUpdateAccount", "Mail", serverId, account.Email, ct).ConfigureAwait(false);
        return MapAccount(account);
    }

    public async Task DeleteAccountAsync(int serverId, int accountId, CancellationToken ct = default)
    {
        var account = await GetAccountForServerAsync(serverId, accountId, ct).ConfigureAwait(false);

        var domain = account.MailDomain.Name;
        var email = account.Email;

        await repo.DeleteAccountAsync(account, ct).ConfigureAwait(false);

        // Typed op via mail-manage (replaces the dead `sed -i ... && postmap ...` shell). The parent
        // domain travels in env so the helper can re-validate the account belongs to it.
        await QueueTaskAsync(ServerTaskFactory.Operation(serverId, $"Mail - delete account {email}",
            OperationKind.MailDeleteAccount, target: email,
            environmentVariables: new Dictionary<string, string> { [MailSetupEnv.Domain] = domain },
            timeoutSeconds: 15), ct).ConfigureAwait(false);

        await audit.LogAsync("MailDeleteAccount", "Mail", serverId, email, ct).ConfigureAwait(false);
    }

    public async Task<MailTaskQueuedDto> ExecuteActionAsync(int serverId, MailActionRequest request, CancellationToken ct = default)
    {
        if (!s_actions.TryGetValue(request.Action, out var operation))
            throw new BadRequestException($"Unsupported mail action '{request.Action}'.");
        var (kind, target) = operation;

        await EnsureMailManageableAsync(serverId, ct).ConfigureAwait(false);
        if (request.Action == MailAction.ApplyRetryTuning)
            await EnsureRetryTuningHelperAsync(serverId, ct).ConfigureAwait(false);
        var task = ServerTaskFactory.Operation(serverId, $"Mail - {request.Action}", kind, target, timeoutSeconds: 90);
        await QueueTaskAsync(task, ct).ConfigureAwait(false);
        await audit.LogAsync($"Mail{request.Action}", "Mail", serverId, request.Action.ToString(), ct).ConfigureAwait(false);
        return new MailTaskQueuedDto { TaskId = task.Id };
    }

    public async Task<MailTaskQueuedDto> GetLogsAsync(int serverId, MailLogRequest request, CancellationToken ct = default)
    {
        if (!MailValidation.IsValidManagedUnit(request.LogType))
            throw new BadRequestException("Invalid log source.");
        if (!MailValidation.IsValidLogLines(request.Lines))
            throw new BadRequestException("Invalid line count.");
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MailSetupEnv.LogLines] = request.Lines.ToString(CultureInfo.InvariantCulture)
        };
        if (!string.IsNullOrEmpty(request.Filter))
        {
            if (!MailValidation.IsValidLogFilter(request.Filter))
                throw new BadRequestException("Invalid log filter.");
            env[MailSetupEnv.LogFilter] = request.Filter;
        }

        await EnsureMailManageableAsync(serverId, ct).ConfigureAwait(false);
        var task = ServerTaskFactory.Operation(serverId, $"Mail logs - {request.LogType}",
            OperationKind.MailGetLogs, request.LogType, env, timeoutSeconds: 30);
        await QueueTaskAsync(task, ct).ConfigureAwait(false);
        return new MailTaskQueuedDto { TaskId = task.Id };
    }

    public async Task<MailTaskQueuedDto> SetupAsync(int serverId, MailSetupRequest request, CancellationToken ct = default)
    {
        if (!MailCommandHelper.IsValidDomainName(request.Hostname))
            throw new BadRequestException("Invalid hostname.");
        if (!MailCommandHelper.IsValidDomainName(request.Domain))
            throw new BadRequestException("Invalid domain.");
        if (!MailCommandHelper.IsValidEmail(request.AdminEmail))
            throw new BadRequestException("Invalid admin email.");
        if (!request.AdminEmail.EndsWith("@" + request.Domain, StringComparison.OrdinalIgnoreCase))
            throw new BadRequestException("The admin address must belong to the mail domain.");
        if (!MailCommandHelper.IsValidPassword(request.AdminPassword))
            throw new BadRequestException("Admin password must be 8–128 characters and contain no control characters.");
        if (!MailCommandHelper.IsValidDkimSelector(request.DkimSelector))
            throw new BadRequestException("Invalid DKIM selector.");
        if (!MailValidation.IsValidQuotaMb(request.QuotaMb))
            throw new BadRequestException("Invalid mailbox quota.");

        // F6: gate on the server's reported mail-setup capability so the UI never queues an action the
        // agent cannot perform - without the aetheus-mail sudoers grant the helper's `sudo -n` would
        // refuse in silence. Mirrors the package-manage gate in ServerServiceManager.
        var server = await serverRepo.FindServerAsync(serverId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"Server {serverId} not found.");
        if (!server.MailSetupAvailable)
            throw new BadRequestException(
                "Mail setup is not enabled on this server. Re-run the agent installer with the mail-setup " +
                "capability (--enable-mail-setup) to grant the controlled-sudo helper.");
        // Serialize setup checks, database records and task queuing per server so concurrent
        // requests observe the open task before creating another setup.
        var gate = s_setupGates.GetOrAdd(serverId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await QueueSetupAsync(serverId, request, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<MailTaskQueuedDto> QueueSetupAsync(int serverId, MailSetupRequest request, CancellationToken ct)
    {
        // Reject setup while a setup task is already open.
        if (await repo.HasOpenTaskAsync(serverId, MailStackPurgeService.StackRebuildKinds, ct).ConfigureAwait(false))
            throw new ConflictException(SetupAlreadyRunning);

        // S-FEAT-W8KN: dispatch the typed MailSetup operation instead of a free-form shell pipeline.
        // The non-root agent could never run the old apt/postconf/systemctl pipeline directly; it now
        // invokes the root-owned `mail-setup` helper through the argv-exact aetheus-mail sudoers
        // grant. The domain is the operation target; hostname/selector/email/quota travel as env vars,
        // and the admin password is piped to the helper over stdin (never argv - off the process list).
        var masterPassword = MailMasterAccess.NewPassword();
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MailSetupEnv.Hostname] = request.Hostname,
            [MailSetupEnv.DkimSelector] = request.DkimSelector,
            [MailSetupEnv.AdminEmail] = request.AdminEmail,
            [MailSetupEnv.QuotaMb] = request.QuotaMb.ToString(CultureInfo.InvariantCulture),
            [MailSetupEnv.AdminPassword] = request.AdminPassword,
            [MailSetupEnv.SpamFilter] = request.EnableSpamFilter ? "rspamd" : "none",
            // Recette R1-41 / R3-50: the administrator access is (re)created with the stack, so a reinstall
            // brings it back; mail-setup hands this password to mail-manage master-set (helper version 4).
            [MailSetupEnv.MasterPassword] = masterPassword
        };

        var task = ServerTaskFactory.Operation(serverId, $"{MailTaskNames.SetupPrefix} ({request.Domain})",
            OperationKind.MailSetup, request.Domain, env, timeoutSeconds: 900);
        // F-001 / F11b: the admin password rides in EnvironmentVariables. Encrypt it at rest (AES-256)
        // like every other secret-bearing task instead of leaving plaintext JSON in the DB - the
        // backend decrypts it only when the task is dispatched to the agent (TaskService env mapping).
        task.EnvironmentVariables = TaskEnvProtection.Protect(encryption, JsonSerializer.Serialize(env));

        // Persist domain records before queuing setup so a database failure cannot leave an
        // unreported task running.
        try
        {
            await RecordSetupAsync(serverId, request, ct).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // The per-server gate only spans this process: a concurrent writer elsewhere (another replica,
            // the heartbeat inventory adopting the domain) recorded it first. Nothing is queued.
            throw new ConflictException(SetupAlreadyRunning);
        }
        await QueueTaskAsync(task, ct).ConfigureAwait(false);
        // Stored once the task is queued, like the no-reply password (R1-16): a refused setup keeps the old one.
        await masterSecrets.StoreAsync(serverId, masterPassword, ct).ConfigureAwait(false);
        await StoreSenderPasswordAsync(request.AdminEmail, request.AdminPassword, ct).ConfigureAwait(false);

        await audit.LogAsync("MailSetup", "Mail", serverId, request.Domain, ct).ConfigureAwait(false);
        // The page keeps "Configurer" busy until this task completes.
        return new MailTaskQueuedDto { TaskId = task.Id };
    }

    /// <summary>Creates the domain and its admin mailbox, or updates them when a previous setup (or the
    /// heartbeat inventory) already recorded them: a setup can be run again on a known domain.</summary>
    private async Task RecordSetupAsync(int serverId, MailSetupRequest request, CancellationToken ct)
    {
        // The address is unique across every server: one recorded on another server's domain is a real
        // conflict, refused before anything is written or queued.
        var account = await repo.FindAccountByEmailAsync(request.AdminEmail, ct).ConfigureAwait(false);
        if (account is not null && account.MailDomain.ServerId != serverId)
            throw new ConflictException($"The address {request.AdminEmail} already belongs to a mail domain on another server.");

        var domain = await repo.FindDomainByNameAsync(serverId, request.Domain, ct).ConfigureAwait(false);
        if (domain is null)
        {
            domain = new MailDomain
            {
                ServerId = serverId,
                Name = request.Domain,
                DkimSelector = request.DkimSelector
            };
            await repo.AddDomainAsync(domain, ct).ConfigureAwait(false);
        }
        else
        {
            domain.DkimSelector = request.DkimSelector;
            domain.IsActive = true;
            await repo.UpdateDomainAsync(domain, ct).ConfigureAwait(false);
        }

        if (account is null)
        {
            await repo.AddAccountAsync(new MailAccount
            {
                MailDomainId = domain.Id,
                Email = request.AdminEmail,
                QuotaMb = request.QuotaMb,
                IsActive = true
            }, ct).ConfigureAwait(false);
            return;
        }

        account.MailDomainId = domain.Id;
        account.QuotaMb = request.QuotaMb;
        account.IsActive = true;
        await repo.UpdateAccountAsync(account, ct).ConfigureAwait(false);
    }

    public async Task<MailDnsRecordsDto> GetDnsRecordsAsync(int serverId, int domainId, CancellationToken ct = default)
    {
        var domain = await repo.GetDomainAsync(serverId, domainId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"Mail domain {domainId} not found.");
        var state = await repo.GetStateAsync(serverId, ct).ConfigureAwait(false);

        // Use the DKIM public key reported in heartbeat inventory, reading it on demand through
        // mail-manage only while unknown.
        var records = MailDnsRecordFactory.Build(domain, state?.Hostname);
        if (records.DkimKeyStatus != MailDkimKeyStatus.Available)
        {
            // SITE-OFFLINE-001: an offline agent cannot read the key and the queue refuses the task (409).
            // Reading the records must not fail for that: every other record is computed here, and the DKIM
            // one is returned as unavailable with its reason.
            var server = await serverRepo.FindServerAsync(serverId, ct).ConfigureAwait(false);
            if (server?.Status != ServerStatus.Online)
                return records with { DkimKeyStatus = MailDkimKeyStatus.AgentOffline };
            await QueueTaskAsync(ServerTaskFactory.Operation(serverId, $"Mail - read DKIM key ({domain.Name})",
                OperationKind.MailDkimRead, domain.Name,
                new Dictionary<string, string> { [MailSetupEnv.DkimSelector] = domain.DkimSelector },
                timeoutSeconds: 30), ct).ConfigureAwait(false);
        }

        return records;
    }

    private static MailDomainDto MapDomain(MailDomain domain) => new()
    {
        Id = domain.Id,
        Name = domain.Name,
        IsActive = domain.IsActive,
        DkimSelector = domain.DkimSelector,
        HasSpf = domain.HasSpf,
        HasDkim = domain.HasDkim,
        HasDmarc = domain.HasDmarc,
        Source = domain.Source,
        MissingSince = domain.MissingSince,
        CreatedAt = domain.CreatedAt
    };

    private static PaginatedResult<T> Page<T>(IEnumerable<T> items, int total, int page, int pageSize) => new()
    {
        Items = items.ToList(),
        TotalCount = total,
        Page = page,
        PageSize = pageSize
    };

    private static MailAccountDto MapAccount(MailAccount account) => new()
    {
        Id = account.Id,
        Email = account.Email,
        QuotaMb = account.QuotaMb,
        IsActive = account.IsActive,
        Domain = account.Email[(account.Email.IndexOf('@') + 1)..],
        UsedMb = account.UsedMb,
        Source = account.Source,
        MissingSince = account.MissingSince,
        CreatedAt = account.CreatedAt
    };

    private static MailAliasDto MapAlias(MailAlias alias) => new()
    {
        Id = alias.Id,
        SourceEmail = alias.SourceEmail,
        DestinationEmail = alias.DestinationEmail,
        IsActive = alias.IsActive,
        Source = alias.Source,
        MissingSince = alias.MissingSince,
        CreatedAt = alias.CreatedAt
    };

    private static ServerTask BuildAddAliasTask(int serverId, string source, string destination)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MailSetupEnv.AliasDestination] = destination
        };
        return ServerTaskFactory.Operation(serverId, $"Mail - alias {source} → {destination}",
            OperationKind.MailAddAlias, source, env, timeoutSeconds: 15);
    }

    // S-FEAT-W8KN: every incremental mail op runs through the root-owned mail-manage helper, which the
    // installer only deposits with the mail-setup capability (the aetheus-mail sudoers grant). Gate
    // here so the UI never queues an op the non-root agent's `sudo -n` would silently refuse - mirrors
    // SetupAsync's MailSetupAvailable check.
    private async Task EnsureMailManageableAsync(int serverId, CancellationToken ct)
    {
        var server = await serverRepo.FindServerAsync(serverId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"Server {serverId} not found.");
        if (!server.MailSetupAvailable)
            throw new BadRequestException(
                "Mail management is not enabled on this server. Re-run the agent installer with the mail-setup " +
                "capability (--enable-mail-setup) to grant the controlled-sudo helper.");
    }

    private async Task EnsureQuotaHelperAsync(int serverId, CancellationToken ct)
    {
        var state = await repo.GetStateAsync(serverId, ct).ConfigureAwait(false);
        if (state is null || state.HelperVersion < MailHelperVersions.QuotaMinimum)
            throw new BadRequestException(
                $"The mail helper of this server cannot enforce a mailbox quota (version {state?.HelperVersion ?? 0}, " +
                $"{MailHelperVersions.QuotaMinimum} required). Update the agent on this server (re-run the agent installer with --enable-mail-setup), then retry.");
    }

    /// <summary>The first mail-manage helper version that has the <c>retry-tuning</c> sub-command.</summary>
    internal const int RetryTuningMinimumHelperVersion = 6;

    private async Task EnsureRetryTuningHelperAsync(int serverId, CancellationToken ct)
    {
        var state = await repo.GetStateAsync(serverId, ct).ConfigureAwait(false);
        if (state is null || state.HelperVersion < RetryTuningMinimumHelperVersion)
            throw new BadRequestException(
                $"The mail helper of this server cannot apply the retry timings (version {state?.HelperVersion ?? 0}, " +
                $"{RetryTuningMinimumHelperVersion} required). Update the agent on this server (re-run the agent installer with --enable-mail-setup), then retry.");
    }

    // --- Aliases ---

    public async Task<List<MailAliasDto>> GetAliasesAsync(int serverId, CancellationToken ct = default)
    {
        var aliases = await repo.GetAliasesAsync(serverId, ct).ConfigureAwait(false);
        return aliases.Select(MapAlias).ToList();
    }

    public async Task<PaginatedResult<MailAliasDto>> GetAliasesAsync(
        int serverId, PaginationRequest request, CancellationToken ct = default)
    {
        var (page, pageSize) = request.Normalize();
        var (items, total) = await repo.GetAliasesPagedAsync(
            serverId, request.Search, page, pageSize, request.SortBy, request.SortDescending, ct, request.Filters).ConfigureAwait(false);
        return Page(items.Select(MapAlias), total, page, pageSize);
    }

    public async Task<MailAliasDto> CreateAliasAsync(int serverId, int domainId, CreateMailAliasRequest request, CancellationToken ct = default)
    {
        var domain = await repo.GetDomainAsync(serverId, domainId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"Mail domain {domainId} not found.");

        if (!MailCommandHelper.IsValidEmail(request.SourceEmail))
            throw new BadRequestException("Invalid source email address.");

        if (!MailCommandHelper.IsValidEmail(request.DestinationEmail))
            throw new BadRequestException("Invalid destination email address.");

        await EnsureMailManageableAsync(serverId, ct).ConfigureAwait(false);

        var alias = new MailAlias
        {
            MailDomainId = domain.Id,
            SourceEmail = request.SourceEmail,
            DestinationEmail = request.DestinationEmail
        };
        await repo.AddAliasAsync(alias, ct).ConfigureAwait(false);

        // S-FEAT-W8KN: typed MailAddAlias via the mail-manage helper. Source email is the target; the
        // destination rides in an env var. No secret involved. Queued via QueueTaskAsync (TaskQueued broadcast).
        await QueueTaskAsync(BuildAddAliasTask(serverId, request.SourceEmail, request.DestinationEmail), ct).ConfigureAwait(false);

        await audit.LogAsync("MailAliasCreated", "Mail", serverId, request.SourceEmail, ct).ConfigureAwait(false);
        return MapAlias(alias);
    }

    public async Task<MailAliasDto> UpdateAliasAsync(int serverId, int aliasId, UpdateMailAliasRequest request, CancellationToken ct = default)
    {
        var alias = await GetAliasForServerAsync(serverId, aliasId, ct).ConfigureAwait(false);

        if (request.IsActive.HasValue)
            alias.IsActive = request.IsActive.Value;
        if (request.DestinationEmail is not null)
        {
            if (!MailCommandHelper.IsValidEmail(request.DestinationEmail))
                throw new BadRequestException("Invalid destination email address.");
            alias.DestinationEmail = request.DestinationEmail;
        }

        await EnsureMailManageableAsync(serverId, ct).ConfigureAwait(false);
        await repo.UpdateAliasAsync(alias, ct).ConfigureAwait(false);

        // Rebuild the alias on the server (add-alias is idempotent: the helper rewrites the line).
        await QueueTaskAsync(BuildAddAliasTask(serverId, alias.SourceEmail, alias.DestinationEmail), ct).ConfigureAwait(false);

        await audit.LogAsync("MailAliasUpdated", "Mail", serverId, alias.SourceEmail, ct).ConfigureAwait(false);
        return MapAlias(alias);
    }

    public async Task DeleteAliasAsync(int serverId, int aliasId, CancellationToken ct = default)
    {
        var alias = await GetAliasForServerAsync(serverId, aliasId, ct).ConfigureAwait(false);

        await repo.DeleteAliasAsync(alias, ct).ConfigureAwait(false);

        // Typed op via mail-manage (replaces the dead `sed -i ... && postmap ...` shell).
        await QueueTaskAsync(ServerTaskFactory.Operation(serverId, $"Mail - remove alias {alias.SourceEmail}",
            OperationKind.MailRemoveAlias, target: alias.SourceEmail, timeoutSeconds: 15), ct).ConfigureAwait(false);

        await audit.LogAsync("MailAliasDeleted", "Mail", serverId, alias.SourceEmail, ct).ConfigureAwait(false);
    }

    // --- DKIM key rotation ---

    public async Task<DkimRotationResultDto> RotateDkimKeyAsync(int serverId, int domainId, DkimRotationRequest request, CancellationToken ct = default)
    {
        var domain = await repo.GetDomainAsync(serverId, domainId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"Mail domain {domainId} not found.");

        if (!MailCommandHelper.IsValidDkimSelector(request.NewSelector))
            throw new BadRequestException("Invalid DKIM selector.");

        await EnsureMailManageableAsync(serverId, ct).ConfigureAwait(false);

        var oldSelector = domain.DkimSelector;
        domain.DkimSelector = request.NewSelector;
        await repo.UpdateDomainAsync(domain, ct).ConfigureAwait(false);

        // S-FEAT-W8KN: typed MailDkimRotate via the mail-manage helper. The domain is the target; the new
        // selector rides in an env var. The helper regenerates the key, rewrites KeyTable/SigningTable,
        // restarts opendkim and emits the public-key TXT record on stdout. Queued via QueueTaskAsync (broadcast).
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MailSetupEnv.NewSelector] = request.NewSelector
        };
        await QueueTaskAsync(ServerTaskFactory.Operation(serverId,
            $"Mail - rotate DKIM key for {domain.Name} ({oldSelector} → {request.NewSelector})",
            OperationKind.MailDkimRotate, domain.Name, env, timeoutSeconds: 30), ct).ConfigureAwait(false);

        await audit.LogAsync("DkimKeyRotated", "Mail", serverId, $"{domain.Name}: {oldSelector} → {request.NewSelector}", ct).ConfigureAwait(false);

        return new DkimRotationResultDto
        {
            Domain = domain.Name,
            OldSelector = oldSelector,
            NewSelector = request.NewSelector,
            DnsRecordName = $"{request.NewSelector}._domainkey.{domain.Name}",
            DnsRecordValue = "(key will be generated by server task)"
        };
    }
}
