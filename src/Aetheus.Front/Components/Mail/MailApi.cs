// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Aetheus.Shared.Components.Organizations;
using Microsoft.AspNetCore.WebUtilities;

namespace Aetheus.Front.Components.Mail;

/// <summary>Mail server state, domains, mailboxes, aliases and DKIM key rotation.</summary>
public sealed class MailApi(HttpClient http) : ApiClientBase(http)
{
    public async Task<MailDataDto> GetMailStateAsync(int serverId, CancellationToken ct = default)
    {
        return await Http.GetFromJsonAsync<MailDataDto>($"api/servers/{serverId}/mail", JsonOptions.Web, ct) ?? new();
    }


    public async Task<PaginatedResult<MailDomainDto>> GetMailDomainsPageAsync(
        int serverId, int page, int pageSize, string? search = null, string? sortBy = null, bool sortDescending = false,
        IReadOnlyList<Aetheus.Shared.Components.Shared.GridFilter>? filters = null)
    {
        var query = BuildPaginationQuery(page, pageSize, search, sortBy, sortDescending);
        // The grid's column header filters.
        return await Http.GetFromJsonAsync<PaginatedResult<MailDomainDto>>(GridColumnFilters.AddTo(
            QueryHelpers.AddQueryString($"api/servers/{serverId}/mail/domains", query), filters), JsonOptions.Web) ?? new();
    }


    public async Task<List<MailDomainDto>> GetMailDomainsAsync(int serverId)
    {
        const int pageSize = 100;
        var items = new List<MailDomainDto>();
        for (var page = 1; ; page++)
        {
            var result = await GetMailDomainsPageAsync(serverId, page, pageSize);
            items.AddRange(result.Items);
            if (items.Count >= result.TotalCount || result.Items.Count == 0) return items;
        }
    }


    public async Task<MailDomainDto?> GetMailDomainAsync(int serverId, int domainId, CancellationToken ct = default)
    {
        return await Http.GetFromJsonAsync<MailDomainDto>($"api/servers/{serverId}/mail/domains/{domainId}", JsonOptions.Web, ct);
    }


    public Task<MailDomainDto?> CreateMailDomainAsync(int serverId, CreateMailDomainRequest request, CancellationToken ct = default)
        => PostJsonAsync<CreateMailDomainRequest, MailDomainDto>($"api/servers/{serverId}/mail/domains", request, ct);


    public Task<MailDomainDto?> UpdateMailDomainAsync(int serverId, int domainId, UpdateMailDomainRequest request, CancellationToken ct = default)
        => PutJsonAsync<UpdateMailDomainRequest, MailDomainDto>($"api/servers/{serverId}/mail/domains/{domainId}", request, ct);


    public Task<ApiStatus> DeleteMailDomainAsync(int serverId, int domainId, CancellationToken ct = default)
        => DeleteAsync($"api/servers/{serverId}/mail/domains/{domainId}", ct);


    public async Task<PaginatedResult<MailAccountDto>> GetMailAccountsPageAsync(
        int serverId, int page, int pageSize, string? search = null, string? sortBy = null, bool sortDescending = false,
        IReadOnlyList<Aetheus.Shared.Components.Shared.GridFilter>? filters = null)
    {
        var query = BuildPaginationQuery(page, pageSize, search, sortBy, sortDescending);
        // The grid's column header filters.
        return await Http.GetFromJsonAsync<PaginatedResult<MailAccountDto>>(GridColumnFilters.AddTo(
            QueryHelpers.AddQueryString($"api/servers/{serverId}/mail/accounts", query), filters), JsonOptions.Web) ?? new();
    }


    public async Task<List<MailAccountDto>> GetMailAccountsAsync(int serverId)
    {
        const int pageSize = 100;
        var items = new List<MailAccountDto>();
        for (var page = 1; ; page++)
        {
            var result = await GetMailAccountsPageAsync(serverId, page, pageSize);
            items.AddRange(result.Items);
            if (items.Count >= result.TotalCount || result.Items.Count == 0) return items;
        }
    }


    public Task<MailAccountDto?> CreateMailAccountAsync(int serverId, CreateMailAccountRequest request, CancellationToken ct = default)
        => PostJsonAsync<CreateMailAccountRequest, MailAccountDto>($"api/servers/{serverId}/mail/accounts", request, ct);


    public Task<MailAccountDto?> UpdateMailAccountAsync(int serverId, int accountId, UpdateMailAccountRequest request, CancellationToken ct = default)
        => PutJsonAsync<UpdateMailAccountRequest, MailAccountDto>($"api/servers/{serverId}/mail/accounts/{accountId}", request, ct);


    public Task<ApiStatus> DeleteMailAccountAsync(int serverId, int accountId, CancellationToken ct = default)
        => DeleteAsync($"api/servers/{serverId}/mail/accounts/{accountId}", ct);


    public Task<ApiStatus> ExecuteMailActionAsync(int serverId, MailActionRequest request, CancellationToken ct = default)
        => PostJsonNoBodyAsync<MailActionRequest>($"api/servers/{serverId}/mail/action", request, ct);


    /// <summary>Queues a mail action and returns the id of its task (to follow its output).</summary>
    public Task<MailTaskQueuedDto?> QueueMailActionAsync(int serverId, MailActionRequest request, CancellationToken ct = default)
        => PostJsonAsync<MailActionRequest, MailTaskQueuedDto>($"api/servers/{serverId}/mail/action", request, ct);


    /// <summary>Queues a journal read and returns the id of its task (to fetch its output).</summary>
    public Task<MailTaskQueuedDto?> FetchMailLogsAsync(int serverId, MailLogRequest request, CancellationToken ct = default)
        => PostJsonAsync<MailLogRequest, MailTaskQueuedDto>($"api/servers/{serverId}/mail/logs", request, ct);


    public Task<ApiStatus> GetMailLogsAsync(int serverId, MailLogRequest request, CancellationToken ct = default)
        => PostJsonNoBodyAsync<MailLogRequest>($"api/servers/{serverId}/mail/logs", request, ct);


    /// <summary>Queues the full setup and returns the id of its task (null when refused).</summary>
    public Task<MailTaskQueuedDto?> SetupMailAsync(int serverId, MailSetupRequest request, CancellationToken ct = default)
        => PostJsonAsync<MailSetupRequest, MailTaskQueuedDto>($"api/servers/{serverId}/mail/setup", request, ct);


    public async Task<MailDnsRecordsDto?> GetMailDnsRecordsAsync(int serverId, int domainId, CancellationToken ct = default)
    {
        return await Http.GetFromJsonAsync<MailDnsRecordsDto>($"api/servers/{serverId}/mail/domains/{domainId}/dns", JsonOptions.Web, ct);
    }


    public async Task<PaginatedResult<MailAliasDto>> GetMailAliasesPageAsync(
        int serverId, int page, int pageSize, string? search = null, string? sortBy = null, bool sortDescending = false,
        IReadOnlyList<Aetheus.Shared.Components.Shared.GridFilter>? filters = null)
    {
        var query = BuildPaginationQuery(page, pageSize, search, sortBy, sortDescending);
        // The grid's column header filters.
        return await Http.GetFromJsonAsync<PaginatedResult<MailAliasDto>>(GridColumnFilters.AddTo(
            QueryHelpers.AddQueryString($"api/servers/{serverId}/mail/aliases", query), filters), JsonOptions.Web) ?? new();
    }


    public async Task<List<MailAliasDto>> GetMailAliasesAsync(int serverId)
    {
        const int pageSize = 100;
        var items = new List<MailAliasDto>();
        for (var page = 1; ; page++)
        {
            var result = await GetMailAliasesPageAsync(serverId, page, pageSize);
            items.AddRange(result.Items);
            if (items.Count >= result.TotalCount || result.Items.Count == 0) return items;
        }
    }


    public Task<MailAliasDto?> CreateMailAliasAsync(int serverId, int domainId, CreateMailAliasRequest request, CancellationToken ct = default)
        => PostJsonAsync<CreateMailAliasRequest, MailAliasDto>($"api/servers/{serverId}/mail/domains/{domainId}/aliases", request, ct);


    public Task<MailAliasDto?> UpdateMailAliasAsync(int serverId, int aliasId, UpdateMailAliasRequest request, CancellationToken ct = default)
        => PutJsonAsync<UpdateMailAliasRequest, MailAliasDto>($"api/servers/{serverId}/mail/aliases/{aliasId}", request, ct);


    public Task<ApiStatus> DeleteMailAliasAsync(int serverId, int aliasId, CancellationToken ct = default)
        => DeleteAsync($"api/servers/{serverId}/mail/aliases/{aliasId}", ct);


    public Task<DkimRotationResultDto?> RotateMailDkimKeyAsync(int serverId, int domainId, DkimRotationRequest request, CancellationToken ct = default)
        => PostJsonAsync<DkimRotationRequest, DkimRotationResultDto>($"api/servers/{serverId}/mail/domains/{domainId}/dkim/rotate", request, ct);

    // Mail TLS, spam filtering, delivery, queues, usage, DNS and diagnostics.

    public Task<MailCertificateDto?> GetMailCertificateAsync(int serverId, CancellationToken ct = default)
        => GetJsonAsync<MailCertificateDto>($"api/servers/{serverId}/mail/certificate", ct);


    /// <summary>Obtains the Let's Encrypt certificate of the mail hostname when missing, then installs it.</summary>
    public Task<MailTaskQueuedDto?> RequestMailCertificateAsync(int serverId, RequestMailCertificateRequest request, CancellationToken ct = default)
        => PostJsonAsync<RequestMailCertificateRequest, MailTaskQueuedDto>($"api/servers/{serverId}/mail/certificate", request, ct);


    public Task<MailTaskQueuedDto?> InstallMailCertificateAsync(int serverId, CancellationToken ct = default)
        => PostNoBodyAsync<MailTaskQueuedDto>($"api/servers/{serverId}/mail/certificate/install", ct);


    public Task<SpamFilterConfigDto?> GetSpamFilterAsync(int serverId, CancellationToken ct = default)
        => GetJsonAsync<SpamFilterConfigDto>($"api/servers/{serverId}/mail/spam", ct);


    public Task<MailTaskQueuedDto?> InstallSpamFilterAsync(int serverId, CancellationToken ct = default)
        => PostNoBodyAsync<MailTaskQueuedDto>($"api/servers/{serverId}/mail/spam/install", ct);


    public Task<MailTaskQueuedDto?> UpdateSpamFilterAsync(int serverId, UpdateSpamFilterRequest request, CancellationToken ct = default)
        => PutJsonAsync<UpdateSpamFilterRequest, MailTaskQueuedDto>($"api/servers/{serverId}/mail/spam", request, ct);


    public Task<MailTaskQueuedDto?> LearnSpamAsync(int serverId, LearnSpamRequest request, CancellationToken ct = default)
        => PostJsonAsync<LearnSpamRequest, MailTaskQueuedDto>($"api/servers/{serverId}/mail/spam/learn", request, ct);


    public Task<MailTaskQueuedDto?> SendMailTestAsync(int serverId, MailTestDeliveryRequest request, CancellationToken ct = default)
        => PostJsonAsync<MailTestDeliveryRequest, MailTaskQueuedDto>($"api/servers/{serverId}/mail/test-delivery", request, ct);


    /// <summary>"Réinstaller la messagerie": queues the purge of the whole mail stack (the domain typed back).</summary>
    public Task<MailTaskQueuedDto?> PurgeMailStackAsync(int serverId, MailPurgeRequest request, CancellationToken ct = default)
        => PostJsonAsync<MailPurgeRequest, MailTaskQueuedDto>($"api/servers/{serverId}/mail/purge", request, ct);


    /// <summary>Recette R1-41 / R3-50: queues "Préparer l'accès administrateur" (administrators only).</summary>
    public Task<MailTaskQueuedDto?> PrepareMasterAccessAsync(int serverId, CancellationToken ct = default)
        => PostNoBodyAsync<MailTaskQueuedDto>($"api/servers/{serverId}/mail/master-access", ct);

    /// <summary>Recette R3-49: deletes the Aetheus mailbox rows the server no longer reports.</summary>
    public Task<MailAccountPurgeResultDto?> PurgeMissingMailAccountsAsync(int serverId, CancellationToken ct = default)
        => PostNoBodyAsync<MailAccountPurgeResultDto>($"api/servers/{serverId}/mail/accounts/purge-missing", ct);


    public Task<MailTaskQueuedDto?> RefreshMailQueueAsync(int serverId, CancellationToken ct = default)
        => PostNoBodyAsync<MailTaskQueuedDto>($"api/servers/{serverId}/mail/queue/refresh", ct);


    public Task<ApiStatus> DeleteQueuedMailAsync(int serverId, string queueId, CancellationToken ct = default)
        => DeleteAsync($"api/servers/{serverId}/mail/queue/{Uri.EscapeDataString(queueId)}", ct);


    public Task<MailTaskQueuedDto?> RefreshMailQuotaAsync(int serverId, CancellationToken ct = default)
        => PostNoBodyAsync<MailTaskQueuedDto>($"api/servers/{serverId}/mail/quota/refresh", ct);


    public Task<int> IngestMailQuotaAsync(int serverId, int taskId, CancellationToken ct = default)
        => PostNoBodyAsync<int>($"api/servers/{serverId}/mail/quota/ingest/{taskId}", ct);


    public Task<MailDnsCheckDto?> VerifyMailDnsAsync(int serverId, int domainId, CancellationToken ct = default)
        => PostNoBodyAsync<MailDnsCheckDto>($"api/servers/{serverId}/mail/domains/{domainId}/dns/verify", ct);


    public Task<MailDiagnosticsDto?> GetMailDiagnosticsAsync(int serverId, CancellationToken ct = default)
        => GetJsonAsync<MailDiagnosticsDto>($"api/servers/{serverId}/mail/diagnostics", ct);

    public Task<MailMxPreviewDto?> PreviewMailMxAsync(int serverId, string domain, CancellationToken ct = default)
        => GetJsonAsync<MailMxPreviewDto>($"api/servers/{serverId}/mail/mx-preview?domain={Uri.EscapeDataString(domain)}", ct);

    /// <summary>A record and reverse DNS of the host name the setup assistant proposes.</summary>
    public Task<MailDiagnosticsDto?> CheckMailPrerequisitesAsync(int serverId, string hostname, CancellationToken ct = default)
        => GetJsonAsync<MailDiagnosticsDto>($"api/servers/{serverId}/mail/prerequisites?hostname={Uri.EscapeDataString(hostname)}", ct);

    /// <summary>Queues the outbound port 25 check on the server agent.</summary>
    public Task<MailTaskQueuedDto?> ProbeMailOutboundSmtpAsync(int serverId, CancellationToken ct = default)
        => PostNoBodyAsync<MailTaskQueuedDto>($"api/servers/{serverId}/mail/prerequisites/outbound-smtp", ct);

    /// <summary>Recette R3-23: queues the check of what a mail stack left on the server.</summary>
    public Task<MailTaskQueuedDto?> CheckMailLeftoversAsync(int serverId, CancellationToken ct = default)
        => PostNoBodyAsync<MailTaskQueuedDto>($"api/servers/{serverId}/mail/leftovers-check", ct);
}
