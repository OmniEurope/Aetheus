// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Configuration;
using Microsoft.Extensions.Caching.Memory;

namespace Aetheus.Back.Components.Mail;

public class CachedMailService(IMailService inner, IMemoryCache cache) : IMailService
{
    private static readonly TimeSpan CacheDuration = BackendRuntimeDefaults.InfrastructureCacheDuration;

    public async Task<MailDataDto> GetStateAsync(int serverId, CancellationToken ct = default)
    {
        var key = MailCacheKeys.State(serverId);
        return await cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;
            return await inner.GetStateAsync(serverId, ct).ConfigureAwait(false);
        }).ConfigureAwait(false) ?? new MailDataDto();
    }

    public async Task<List<MailDomainDto>> GetDomainsAsync(int serverId, CancellationToken ct = default)
    {
        var key = MailCacheKeys.Domains(serverId);
        return await cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;
            return await inner.GetDomainsAsync(serverId, ct).ConfigureAwait(false);
        }).ConfigureAwait(false) ?? [];
    }

    public Task<PaginatedResult<MailDomainDto>> GetDomainsAsync(
        int serverId, PaginationRequest request, CancellationToken ct = default) =>
        inner.GetDomainsAsync(serverId, request, ct);

    public async Task<MailDomainDto> GetDomainAsync(int serverId, int domainId, CancellationToken ct = default) =>
        await inner.GetDomainAsync(serverId, domainId, ct).ConfigureAwait(false);

    public async Task<MailDomainDto> CreateDomainAsync(int serverId, CreateMailDomainRequest request, CancellationToken ct = default)
    {
        InvalidateCache(serverId);
        return await inner.CreateDomainAsync(serverId, request, ct).ConfigureAwait(false);
    }

    public async Task<MailDomainDto> UpdateDomainAsync(int serverId, int domainId, UpdateMailDomainRequest request, CancellationToken ct = default)
    {
        InvalidateCache(serverId);
        return await inner.UpdateDomainAsync(serverId, domainId, request, ct).ConfigureAwait(false);
    }

    public async Task DeleteDomainAsync(int serverId, int domainId, CancellationToken ct = default)
    {
        InvalidateCache(serverId);
        await inner.DeleteDomainAsync(serverId, domainId, ct).ConfigureAwait(false);
    }

    public async Task<List<MailAccountDto>> GetAccountsAsync(int serverId, CancellationToken ct = default)
    {
        var key = MailCacheKeys.Accounts(serverId);
        return await cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;
            return await inner.GetAccountsAsync(serverId, ct).ConfigureAwait(false);
        }).ConfigureAwait(false) ?? [];
    }

    public Task<PaginatedResult<MailAccountDto>> GetAccountsAsync(
        int serverId, PaginationRequest request, CancellationToken ct = default) =>
        inner.GetAccountsAsync(serverId, request, ct);

    public async Task<MailAccountDto> CreateAccountAsync(int serverId, CreateMailAccountRequest request, CancellationToken ct = default)
    {
        InvalidateCache(serverId);
        return await inner.CreateAccountAsync(serverId, request, ct).ConfigureAwait(false);
    }

    public async Task<MailAccountDto> UpdateAccountAsync(int serverId, int accountId, UpdateMailAccountRequest request, CancellationToken ct = default)
    {
        InvalidateCache(serverId);
        return await inner.UpdateAccountAsync(serverId, accountId, request, ct).ConfigureAwait(false);
    }

    public async Task DeleteAccountAsync(int serverId, int accountId, CancellationToken ct = default)
    {
        InvalidateCache(serverId);
        await inner.DeleteAccountAsync(serverId, accountId, ct).ConfigureAwait(false);
    }

    public async Task<MailTaskQueuedDto> ExecuteActionAsync(int serverId, MailActionRequest request, CancellationToken ct = default)
    {
        InvalidateCache(serverId);
        return await inner.ExecuteActionAsync(serverId, request, ct).ConfigureAwait(false);
    }

    public async Task<MailTaskQueuedDto> GetLogsAsync(int serverId, MailLogRequest request, CancellationToken ct = default) =>
        await inner.GetLogsAsync(serverId, request, ct).ConfigureAwait(false);

    public async Task<MailTaskQueuedDto> SetupAsync(int serverId, MailSetupRequest request, CancellationToken ct = default)
    {
        InvalidateCache(serverId);
        return await inner.SetupAsync(serverId, request, ct).ConfigureAwait(false);
    }

    public async Task<MailDnsRecordsDto> GetDnsRecordsAsync(int serverId, int domainId, CancellationToken ct = default) =>
        await inner.GetDnsRecordsAsync(serverId, domainId, ct).ConfigureAwait(false);

    // Aliases
    public async Task<List<MailAliasDto>> GetAliasesAsync(int serverId, CancellationToken ct = default) =>
        await inner.GetAliasesAsync(serverId, ct).ConfigureAwait(false);

    public Task<PaginatedResult<MailAliasDto>> GetAliasesAsync(
        int serverId, PaginationRequest request, CancellationToken ct = default) =>
        inner.GetAliasesAsync(serverId, request, ct);

    public async Task<MailAliasDto> CreateAliasAsync(int serverId, int domainId, CreateMailAliasRequest request, CancellationToken ct = default)
    {
        InvalidateCache(serverId);
        return await inner.CreateAliasAsync(serverId, domainId, request, ct).ConfigureAwait(false);
    }

    public async Task<MailAliasDto> UpdateAliasAsync(int serverId, int aliasId, UpdateMailAliasRequest request, CancellationToken ct = default)
    {
        InvalidateCache(serverId);
        return await inner.UpdateAliasAsync(serverId, aliasId, request, ct).ConfigureAwait(false);
    }

    public async Task DeleteAliasAsync(int serverId, int aliasId, CancellationToken ct = default)
    {
        InvalidateCache(serverId);
        await inner.DeleteAliasAsync(serverId, aliasId, ct).ConfigureAwait(false);
    }

    // DKIM rotation
    public async Task<DkimRotationResultDto> RotateDkimKeyAsync(int serverId, int domainId, DkimRotationRequest request, CancellationToken ct = default)
    {
        InvalidateCache(serverId);
        return await inner.RotateDkimKeyAsync(serverId, domainId, request, ct).ConfigureAwait(false);
    }

    private void InvalidateCache(int serverId) => MailCacheKeys.Invalidate(cache, serverId);
}
