// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Mail;

public interface IMailService
{
    Task<MailDataDto> GetStateAsync(int serverId, CancellationToken ct = default);
    Task<List<MailDomainDto>> GetDomainsAsync(int serverId, CancellationToken ct = default);
    Task<PaginatedResult<MailDomainDto>> GetDomainsAsync(
        int serverId, PaginationRequest request, CancellationToken ct = default);
    Task<MailDomainDto> GetDomainAsync(int serverId, int domainId, CancellationToken ct = default);
    Task<MailDomainDto> CreateDomainAsync(int serverId, CreateMailDomainRequest request, CancellationToken ct = default);
    Task<MailDomainDto> UpdateDomainAsync(int serverId, int domainId, UpdateMailDomainRequest request, CancellationToken ct = default);
    Task DeleteDomainAsync(int serverId, int domainId, CancellationToken ct = default);
    Task<List<MailAccountDto>> GetAccountsAsync(int serverId, CancellationToken ct = default);
    Task<PaginatedResult<MailAccountDto>> GetAccountsAsync(
        int serverId, PaginationRequest request, CancellationToken ct = default);
    Task<MailAccountDto> CreateAccountAsync(int serverId, CreateMailAccountRequest request, CancellationToken ct = default);
    Task<MailAccountDto> UpdateAccountAsync(int serverId, int accountId, UpdateMailAccountRequest request, CancellationToken ct = default);
    Task DeleteAccountAsync(int serverId, int accountId, CancellationToken ct = default);
    Task<MailTaskQueuedDto> ExecuteActionAsync(int serverId, MailActionRequest request, CancellationToken ct = default);
    Task<MailTaskQueuedDto> GetLogsAsync(int serverId, MailLogRequest request, CancellationToken ct = default);
    Task<MailTaskQueuedDto> SetupAsync(int serverId, MailSetupRequest request, CancellationToken ct = default);
    Task<MailDnsRecordsDto> GetDnsRecordsAsync(int serverId, int domainId, CancellationToken ct = default);

    // Aliases
    Task<List<MailAliasDto>> GetAliasesAsync(int serverId, CancellationToken ct = default);
    Task<PaginatedResult<MailAliasDto>> GetAliasesAsync(
        int serverId, PaginationRequest request, CancellationToken ct = default);
    Task<MailAliasDto> CreateAliasAsync(int serverId, int domainId, CreateMailAliasRequest request, CancellationToken ct = default);
    Task<MailAliasDto> UpdateAliasAsync(int serverId, int aliasId, UpdateMailAliasRequest request, CancellationToken ct = default);
    Task DeleteAliasAsync(int serverId, int aliasId, CancellationToken ct = default);

    // DKIM rotation
    Task<DkimRotationResultDto> RotateDkimKeyAsync(int serverId, int domainId, DkimRotationRequest request, CancellationToken ct = default);
}
