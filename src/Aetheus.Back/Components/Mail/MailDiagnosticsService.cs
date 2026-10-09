// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Mail;

public interface IMailDiagnosticsService
{
    Task<MailDiagnosticsDto> GetAsync(int serverId, CancellationToken ct = default);

    Task<MailMxPreviewDto> PreviewMxAsync(string domain, CancellationToken ct = default);

    /// <summary>Verifies the DNS of one domain now and persists its SPF / DKIM / DMARC flags.</summary>
    Task<MailDnsCheckDto> VerifyDomainDnsAsync(int serverId, int domainId, CancellationToken ct = default);

    /// <summary>Recette R2-080: the DNS prerequisites of a first configuration, for the host name the setup
    /// assistant proposes: its A record resolves to the server, the server's reverse DNS names it.</summary>
    Task<MailDiagnosticsDto> CheckPrerequisitesAsync(int serverId, string hostname, CancellationToken ct = default);
}

/// <summary>
/// PLAN-005 lot 7: a "why does my mail not work" checklist computed without SSH, from the last heartbeat
/// inventory and backend DNS lookups. Keys and details are stable codes the UI localises. The listening
/// ports are checked on the server itself by the <c>MailTestConfig</c> task (mail-manage check).
/// </summary>
public sealed class MailDiagnosticsService(
    IMailInventoryRepository inventory,
    IMailRepository mailRepo,
    IServerRepository serverRepo,
    IMailDnsResolver resolver,
    IMailDnsVerifier verifier,
    TimeProvider timeProvider) : IMailDiagnosticsService
{
    internal const int CurrentHelperVersion = 3;
    internal static readonly TimeSpan CertificateWarning = TimeSpan.FromDays(14);
    private const int MaxDomainsChecked = 20;

    public async Task<MailDiagnosticsDto> GetAsync(int serverId, CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var state = await inventory.GetStateAsync(serverId, ct).ConfigureAwait(false);
        if (state is null)
            return new MailDiagnosticsDto { CheckedAt = now, Items = [Item("installed", MailCheckVerdict.Missing)] };

        var server = await serverRepo.FindServerAsync(serverId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"Server {serverId} not found.");
        var items = new List<MailDiagnosticItemDto>
        {
            Item("installed", MailCheckVerdict.Ok),
            server.MailSetupAvailable ? Item("capability", MailCheckVerdict.Ok) : Item("capability", MailCheckVerdict.Missing, "enable-mail-setup"),
            state.HelperVersion >= CurrentHelperVersion
                ? Item("helper-version", MailCheckVerdict.Ok, state.HelperVersion.ToString(CultureInfo.InvariantCulture))
                : Item("helper-version", MailCheckVerdict.Mismatch, state.HelperVersion.ToString(CultureInfo.InvariantCulture)),
            Services(state),
            SpamFilter(state),
            Certificate(state, now),
            state.QueueSize == 0
                ? Item("queue", MailCheckVerdict.Ok)
                : Item("queue", MailCheckVerdict.Mismatch, state.QueueSize.ToString(CultureInfo.InvariantCulture))
        };
        items.Add(await HostnameAsync(state.Hostname, server.IpAddress, ct).ConfigureAwait(false));
        items.Add(await ReverseDnsAsync(state.Hostname, server.IpAddress, ct).ConfigureAwait(false));
        items.AddRange(await DomainDnsAsync(serverId, state.Hostname, ct).ConfigureAwait(false));
        return new MailDiagnosticsDto { CheckedAt = now, Items = items };
    }

    public async Task<MailDiagnosticsDto> CheckPrerequisitesAsync(int serverId, string hostname, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(hostname) || hostname.Length > 253 || !MailValidation.IsValidDomainName(hostname))
            throw new BadRequestException("Invalid mail host name.");
        var server = await serverRepo.FindServerAsync(serverId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"Server {serverId} not found.");
        var name = hostname.Trim().ToLowerInvariant();
        return new MailDiagnosticsDto
        {
            CheckedAt = timeProvider.GetUtcNow().UtcDateTime,
            Items =
            [
                await HostnameAsync(name, server.IpAddress, ct).ConfigureAwait(false),
                await ReverseDnsAsync(name, server.IpAddress, ct).ConfigureAwait(false)
            ]
        };
    }

    public async Task<MailDnsCheckDto> VerifyDomainDnsAsync(int serverId, int domainId, CancellationToken ct = default)
    {
        var domain = await inventory.GetTrackedDomainAsync(serverId, domainId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"Mail domain {domainId} not found.");
        var state = await inventory.GetStateAsync(serverId, ct).ConfigureAwait(false);
        var result = await verifier.VerifyAsync(domain, state?.Hostname, ct).ConfigureAwait(false);
        await inventory.SaveChangesAsync(ct).ConfigureAwait(false);
        return result;
    }

    public async Task<MailMxPreviewDto> PreviewMxAsync(string domain, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(domain) || domain.Length > 253 || !MailValidation.IsValidDomainName(domain))
            throw new BadRequestException("Invalid mail domain.");

        try
        {
            var hosts = await resolver.MxAsync(domain, ct).ConfigureAwait(false);
            if (hosts.Count > 20)
                return new MailMxPreviewDto { Domain = domain, Verdict = MailCheckVerdict.Unknown };
            return new MailMxPreviewDto
            {
                Domain = domain,
                Hosts = hosts.ToList(),
                Verdict = hosts.Count == 0 ? MailCheckVerdict.Missing : MailCheckVerdict.Ok
            };
        }
        catch (MailDnsLookupException)
        {
            return new MailMxPreviewDto { Domain = domain, Verdict = MailCheckVerdict.Unknown };
        }
    }

    private static MailDiagnosticItemDto Services(MailState state)
    {
        var stopped = new[] { ("postfix", state.IsPostfixRunning), ("dovecot", state.IsDovecotRunning), ("opendkim", state.IsOpenDkimRunning) }
            .Where(s => !s.Item2)
            .Select(s => s.Item1)
            .ToList();
        return stopped.Count == 0
            ? Item("services", MailCheckVerdict.Ok)
            : Item("services", MailCheckVerdict.Mismatch, string.Join(",", stopped));
    }

    private static MailDiagnosticItemDto SpamFilter(MailState state) => (state.IsSpamFilterInstalled, state.IsSpamFilterRunning) switch
    {
        (true, true) => Item("spam-filter", MailCheckVerdict.Ok, state.SpamFilterName),
        (true, false) => Item("spam-filter", MailCheckVerdict.Mismatch, "stopped"),
        _ => Item("spam-filter", MailCheckVerdict.Missing)
    };

    private static MailDiagnosticItemDto Certificate(MailState state, DateTime now)
    {
        if (string.IsNullOrEmpty(state.TlsCertPath)) return Item("tls", MailCheckVerdict.Missing);
        if (!state.TlsIsReadable) return Item("tls", MailCheckVerdict.Unknown, "unreadable");
        if (state.TlsIsSelfSigned) return Item("tls", MailCheckVerdict.Mismatch, "self-signed");
        if (state.TlsExpiresAt is { } expires && expires - now < CertificateWarning)
            return Item("tls", MailCheckVerdict.Mismatch, expires <= now ? "expired" : "expiring");
        return Item("tls", MailCheckVerdict.Ok);
    }

    private async Task<MailDiagnosticItemDto> HostnameAsync(string hostname, string serverIp, CancellationToken ct)
    {
        if (!MailValidation.IsValidDomainName(hostname)) return Item("hostname-dns", MailCheckVerdict.Unknown, "no-hostname");
        // Recette R2-090: the record the line expects, shown to copy when the line is red.
        var record = string.IsNullOrWhiteSpace(serverIp)
            ? Item("hostname-dns", MailCheckVerdict.Unknown)
            : Item("hostname-dns", MailCheckVerdict.Unknown) with
            {
                RecordName = hostname,
                RecordType = IPAddress.TryParse(serverIp, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6 ? "AAAA" : "A",
                RecordValue = serverIp
            };
        try
        {
            var addresses = await resolver.AddressesAsync(hostname, ct).ConfigureAwait(false);
            if (addresses.Count == 0) return record with { Verdict = MailCheckVerdict.Missing, Detail = hostname };
            return addresses.Contains(serverIp)
                ? record with { Verdict = MailCheckVerdict.Ok, Detail = hostname }
                : record with { Verdict = MailCheckVerdict.Mismatch, Detail = $"{hostname}: {string.Join(", ", addresses)}" };
        }
        catch (MailDnsLookupException)
        {
            return record with { Detail = "resolver-error" };
        }
    }

    private async Task<MailDiagnosticItemDto> ReverseDnsAsync(string hostname, string serverIp, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(serverIp)) return Item("reverse-dns", MailCheckVerdict.Unknown, "no-address");
        var record = MailValidation.IsValidDomainName(hostname)
            ? Item("reverse-dns", MailCheckVerdict.Unknown) with { RecordName = serverIp, RecordType = "PTR", RecordValue = hostname }
            : Item("reverse-dns", MailCheckVerdict.Unknown);
        try
        {
            var names = await resolver.PtrAsync(serverIp, ct).ConfigureAwait(false);
            if (names.Count == 0) return record with { Verdict = MailCheckVerdict.Missing, Detail = serverIp };
            return names.Contains(hostname, StringComparer.OrdinalIgnoreCase)
                ? record with { Verdict = MailCheckVerdict.Ok, Detail = serverIp }
                : record with { Verdict = MailCheckVerdict.Mismatch, Detail = $"{serverIp}: {string.Join(", ", names)}" };
        }
        catch (MailDnsLookupException)
        {
            return record with { Detail = "resolver-error" };
        }
    }

    /// <summary>MX, then (recette R2-090) SPF, DKIM and DMARC of every active domain, each with the record to
    /// publish. Read-only: the verifier's <c>CheckAsync</c> leaves the domains' stored flags alone.</summary>
    private async Task<List<MailDiagnosticItemDto>> DomainDnsAsync(int serverId, string hostname, CancellationToken ct)
    {
        var domains = (await mailRepo.GetDomainsAsync(serverId, ct).ConfigureAwait(false))
            .Where(d => d.IsActive)
            .Take(MaxDomainsChecked)
            .ToList();
        var items = new List<MailDiagnosticItemDto>();
        foreach (var domain in domains)
        {
            var records = MailDnsRecordFactory.Build(domain, hostname);
            foreach (var check in await verifier.CheckAsync(domain, hostname, ct).ConfigureAwait(false))
                items.Add(DomainItem(domain.Name, records, check));
        }
        return items;
    }

    private static MailDiagnosticItemDto DomainItem(string domain, MailDnsRecordsDto records, MailDnsRecordCheckDto check) => check.Kind switch
    {
        MailDnsRecordKind.Mx => new MailDiagnosticItemDto
        {
            Key = $"mx:{domain}",
            Verdict = check.Verdict,
            Detail = check.Verdict == MailCheckVerdict.Unknown ? check.Detail
                : check.Observed.Count == 0 ? check.Expected : string.Join(", ", check.Observed),
            RecordName = domain,
            RecordType = "MX",
            RecordValue = records.MxRecord
        },
        MailDnsRecordKind.Spf => TxtItem($"spf:{domain}", check, domain, records.SpfRecord),
        MailDnsRecordKind.Dkim => TxtItem($"dkim:{domain}", check, records.DkimRecord, records.DkimPublicKey),
        _ => TxtItem($"dmarc:{domain}", check, $"_dmarc.{domain}", records.DmarcRecord)
    };

    private static MailDiagnosticItemDto TxtItem(string key, MailDnsRecordCheckDto check, string name, string value) => new()
    {
        Key = key,
        Verdict = check.Verdict,
        Detail = check.Detail,
        RecordName = name,
        RecordType = "TXT",
        RecordValue = value
    };

    private static MailDiagnosticItemDto Item(string key, MailCheckVerdict verdict, string detail = "") =>
        new() { Key = key, Verdict = verdict, Detail = detail };
}
