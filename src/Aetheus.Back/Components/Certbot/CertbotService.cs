// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Certbot;

public class CertbotService(ICertbotRepository repo, IAuditService audit, ITaskService taskService) : ICertbotService
{
    private const int DefaultTimeoutSeconds = 120;

    /// <summary>Recette R1-7: a rehearsal walks every lineage and the weekly one was seen running about ten
    /// minutes, so the 120 s of the other actions bounded the on-demand one far below that.</summary>
    internal const int RenewalCheckTimeoutSeconds = 1500;

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

        var target = "-";
        if (requiresName)
        {
            if (string.IsNullOrWhiteSpace(request.CertificateName))
                throw new BadRequestException($"Certificate name is required for {request.Action}.");
            if (!CertbotCommandHelper.IsValidCertName(request.CertificateName))
                throw new BadRequestException("Invalid certificate name.");
            target = request.CertificateName;
        }

        var name = $"Certbot {request.Action}{(request.CertificateName is not null ? $" - {request.CertificateName}" : "")}";
        await QueueTaskAsync(ServerTaskFactory.Operation(serverId, name, operation, target,
            request.Action == CertbotAction.RenewalCheck ? RenewalCheckTimeoutSeconds : DefaultTimeoutSeconds), ct).ConfigureAwait(false);

        await audit.LogAsync($"Certbot{request.Action}", "Certbot", serverId,
            request.CertificateName ?? request.Action.ToString(), ct).ConfigureAwait(false);
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
        await QueueTaskAsync(ServerTaskFactory.Operation(serverId, $"Certbot create - {request.Domains}",
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
            $"Certbot {request.Action} - {request.CertificateName} - {request.DomainName}",
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
