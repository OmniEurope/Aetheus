// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data.Entities;
using Aetheus.Shared.Components.Servers;

namespace Aetheus.Back.Components.Certbot;

public class CertbotService(ICertbotRepository repo, IAuditService audit, ITaskService taskService) : ICertbotService
{
    private const int DefaultTimeoutSeconds = 120;

    private const string NothingFailedMessage = "No certificate failed the last renewal test, so there is nothing to test again.";

    /// <summary>Recette R1-7: a rehearsal walks every lineage and the weekly one was seen running about ten
    /// minutes, so the 120 s of the other actions bounded the on-demand one far below that.</summary>
    internal const int RenewalCheckTimeoutSeconds = 1500;

    // Recette R-12: every Certbot task name starts with the prefix the Certbot page follows (ServerTaskNames).
    private static string TaskName(string description) => $"{ServerTaskNames.CertbotPrefix} {description}";

    private Task QueueTaskAsync(ServerTask task, CancellationToken ct = default)
        => TaskQueuePersistence.PersistAndNotifyAsync(repo.AddTaskAsync, taskService, task, ct);

    public async Task<List<CertbotCertificateDto>> GetCertificatesAsync(int serverId, CancellationToken ct = default)
    {
        var certs = await repo.GetCertificatesAsync(serverId, ct).ConfigureAwait(false);
        return certs.Select(c => new CertbotCertificateDto
        {
            Name = c.Name,
            Domains = DeserializeDomains(c.Domains),
            ExpiryDate = c.ExpiryDate,
            CertPath = c.CertPath,
            KeyPath = c.KeyPath,
            Authenticator = c.Authenticator,
            WebrootPath = c.WebrootPath,
            RenewalConvention = c.RenewalConvention
        }).ToList();
    }

    public async Task ExecuteActionAsync(int serverId, CertbotActionRequest request, CancellationToken ct = default)
    {
        if (request.Action == CertbotAction.RemoveDomain)
        {
            await RemoveDomainAsync(serverId, request, ct).ConfigureAwait(false);
            return;
        }
        if (request.Action == CertbotAction.RenewalCheckFailed)
        {
            await RenewalCheckFailedAsync(serverId, ct).ConfigureAwait(false);
            return;
        }

        // F-32 / GTFOBins: certbot is never run as a free-form `sudo certbot` shell string (the agent's
        // CommandValidator rejects it). Each action is a typed operation the agent dispatches to the
        // root-owned aetheus-certbot-manage helper argv-exact (re-validates the lineage name).
        var (operation, requiresName) = request.Action switch
        {
            CertbotAction.Renew => (OperationKind.CertbotRenew, true),
            CertbotAction.RenewAll => (OperationKind.CertbotRenewAll, false),
            CertbotAction.Delete => (OperationKind.CertbotDelete, true),
            CertbotAction.RevokeAndDelete => (OperationKind.CertbotRevoke, true),
            CertbotAction.Normalize => (OperationKind.CertbotNormalize, false),
            CertbotAction.RenewalCheck => (OperationKind.CertbotRenewalCheck, false),
            _ => throw new BadRequestException($"Unknown Certbot action: {request.Action}")
        };

        var target = ResolveTarget(request, requiresName);

        if (operation == OperationKind.CertbotRenewalCheck && target != CertbotRenewalCheckTarget.All)
            await EnsureHelperTakesNamesAsync(serverId, ct).ConfigureAwait(false);

        var name = TaskName($"{request.Action}{(request.CertificateName is not null ? $" - {request.CertificateName}" : "")}");
        await QueueTaskAsync(ServerTaskFactory.Operation(serverId, name, operation, target,
            request.Action == CertbotAction.RenewalCheck ? RenewalCheckTimeoutSeconds : DefaultTimeoutSeconds), ct).ConfigureAwait(false);

        await audit.LogAsync($"Certbot{request.Action}", "Certbot", serverId,
            request.CertificateName ?? request.Action.ToString(), ct).ConfigureAwait(false);
    }

    // The operation target: the certificate name for the actions that act on one, "-" for those that act on all.
    // Recette R-19: a rehearsal that names a certificate tests that one only; without a name it tests every one.
    private static string ResolveTarget(CertbotActionRequest request, bool requiresName)
    {
        var nameIsOptional = request.Action == CertbotAction.RenewalCheck;
        if (!requiresName && !(nameIsOptional && !string.IsNullOrWhiteSpace(request.CertificateName)))
            return "-";
        if (string.IsNullOrWhiteSpace(request.CertificateName))
            throw new BadRequestException($"Certificate name is required for {request.Action}.");
        if (!CertbotCommandHelper.IsValidCertName(request.CertificateName))
            throw new BadRequestException("Invalid certificate name.");
        return request.CertificateName;
    }

    /// <summary>Recette R-19: rehearses, one after the other in a single task, only the certificates the last test
    /// reported as failed. The names come from the state the server recorded (never from the caller), restricted to
    /// certificates that still exist: a failure of a certificate since deleted has nothing left to test.</summary>
    private async Task RenewalCheckFailedAsync(int serverId, CancellationToken ct)
    {
        var failed = await repo.GetRenewalCheckFailedNamesAsync(serverId, ct).ConfigureAwait(false);
        if (failed.Count == 0)
            throw new BadRequestException(NothingFailedMessage);
        var existing = (await repo.GetCertificatesAsync(serverId, ct).ConfigureAwait(false))
            .Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        var names = failed.Where(n => existing.Contains(n) && CertbotCommandHelper.IsValidCertName(n)).Order(StringComparer.Ordinal).ToList();
        if (names.Count == 0)
            throw new BadRequestException(NothingFailedMessage);
        if (names.Count > CertbotRenewalCheckTarget.MaxNames)
            throw new BadRequestException($"More than {CertbotRenewalCheckTarget.MaxNames} certificates failed: test them one by one, or run the full renewal test.");

        await EnsureHelperTakesNamesAsync(serverId, ct).ConfigureAwait(false);

        await QueueTaskAsync(ServerTaskFactory.Operation(serverId, TaskName($"{CertbotAction.RenewalCheckFailed} ({names.Count})"),
            OperationKind.CertbotRenewalCheck, CertbotRenewalCheckTarget.Of(names), RenewalCheckTimeoutSeconds), ct).ConfigureAwait(false);

        await audit.LogAsync("CertbotRenewalCheckFailed", "Certbot", serverId,
            string.Join(", ", names), ct).ConfigureAwait(false);
    }

    /// <summary>Recette R-19: a helper older than <see cref="CertbotHelperVersion.TargetedRenewalCheck"/> ignores the
    /// names it is given and rehearses every certificate, so the task would say it tested one while it ran them all (and
    /// replaced the recorded result of all). Refused instead, with the way out; a server that never reported its helper
    /// counts as old.</summary>
    private async Task EnsureHelperTakesNamesAsync(int serverId, CancellationToken ct)
    {
        var version = await repo.GetHelperVersionAsync(serverId, ct).ConfigureAwait(false);
        if (version < CertbotHelperVersion.TargetedRenewalCheck)
            throw new BadRequestException(
                $"The Certbot helper of this server cannot test certificates one by one (version {version}, {CertbotHelperVersion.TargetedRenewalCheck} required). " +
                "Update the agent on this server (re-run the agent installer with --enable-certbot-manage), then retry, or test every certificate.");
    }

    public async Task CreateCertificateAsync(int serverId, CertbotCreateRequest request, CancellationToken ct = default)
    {
        if (!CertbotCommandHelper.IsValidDomainList(request.Domains))
            throw new BadRequestException("Invalid domain list.");
        if (!string.IsNullOrWhiteSpace(request.Email) && !CertbotCommandHelper.IsValidEmail(request.Email))
            throw new BadRequestException("Invalid email address.");

        // Uses the typed CertbotObtain operation and root-owned ACME web-root helper, with domains
        // and email passed through the environment rather than argv. Validation failures propagate
        // to the caller.
        var domains = request.Domains
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var env = new Dictionary<string, string>
        {
            ["AETHEUS_CERTBOT_DOMAINS"] = string.Join(",", domains),
            ["AETHEUS_CERTBOT_EMAIL"] = request.Email ?? string.Empty
        };
        await QueueTaskAsync(ServerTaskFactory.Operation(serverId, TaskName($"create - {request.Domains}"),
            OperationKind.CertbotObtain, domains[0], env, 180), ct).ConfigureAwait(false);

        await audit.LogAsync("CertbotCreate", "Certbot", serverId, request.Domains, ct).ConfigureAwait(false);
    }

    /// <summary>Recette R-12: reissues a certificate without one of its names. Only a name the server reported
    /// for that certificate can be taken out, and never its last one; the helper checks both again against
    /// the certificate on disk before certbot runs. The certificate itself is kept.</summary>
    private async Task RemoveDomainAsync(int serverId, CertbotActionRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.CertificateName) || !CertbotCommandHelper.IsValidCertName(request.CertificateName))
            throw new BadRequestException("Invalid certificate name.");
        if (string.IsNullOrWhiteSpace(request.DomainName) || !CertbotCommandHelper.IsValidDomainList(request.DomainName)
            || request.DomainName.Contains(',', StringComparison.Ordinal) || request.DomainName.Any(char.IsControl))
            throw new BadRequestException("Invalid domain name.");

        var certificates = await repo.GetCertificatesAsync(serverId, ct).ConfigureAwait(false);
        var certificate = certificates.FirstOrDefault(c => string.Equals(c.Name, request.CertificateName, StringComparison.Ordinal))
            ?? throw new BadRequestException($"Unknown certificate: {request.CertificateName}.");
        var domains = DeserializeDomains(certificate.Domains);
        if (!domains.Contains(request.DomainName, StringComparer.OrdinalIgnoreCase))
            throw new BadRequestException($"The certificate {request.CertificateName} does not cover {request.DomainName}.");
        if (domains.Count < 2)
            throw new BadRequestException("A certificate keeps at least one name: delete it instead.");

        var env = new Dictionary<string, string> { [CertbotTaskVariables.RemoveDomain] = request.DomainName };
        await QueueTaskAsync(ServerTaskFactory.Operation(serverId,
            TaskName($"{request.Action} - {request.CertificateName} - {request.DomainName}"),
            OperationKind.CertbotRemoveDomain, request.CertificateName, env, 180), ct).ConfigureAwait(false);

        await audit.LogAsync($"Certbot{request.Action}", "Certbot", serverId,
            $"{request.CertificateName}: {request.DomainName}", ct).ConfigureAwait(false);
    }

    private static List<string> DeserializeDomains(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }
}
