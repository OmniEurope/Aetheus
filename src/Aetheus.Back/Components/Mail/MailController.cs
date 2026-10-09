// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Mail;

[ApiController]
[Route("api/servers/{serverId:int}/mail")]
[Authorize]
[ServiceFilter(typeof(ValidateServerExistsFilter))]
public class MailController(IMailService mailService, IResourceAuthorizationService authz) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<MailDataDto>> GetState(int serverId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Read, ct))
            return Forbid();
        var state = await mailService.GetStateAsync(serverId, ct);
        return Ok(state);
    }

    [HttpGet("domains")]
    public async Task<ActionResult<PaginatedResult<MailDomainDto>>> GetDomains(
        int serverId, [FromQuery] PaginationRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Read, ct))
            return Forbid();
        var domains = await mailService.GetDomainsAsync(serverId, request, ct);
        return Ok(domains);
    }

    [HttpGet("domains/{domainId:int}")]
    public async Task<ActionResult<MailDomainDto>> GetDomain(int serverId, int domainId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Read, ct))
            return Forbid();
        var domain = await mailService.GetDomainAsync(serverId, domainId, ct);
        return Ok(domain);
    }

    [HttpPost("domains")]
    public async Task<ActionResult<MailDomainDto>> CreateDomain(int serverId, [FromBody] CreateMailDomainRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Write, ct))
            return Forbid();
        var domain = await mailService.CreateDomainAsync(serverId, request, ct);
        return CreatedAtAction(nameof(GetDomain), new { serverId, domainId = domain.Id }, domain);
    }

    [HttpPut("domains/{domainId:int}")]
    public async Task<ActionResult<MailDomainDto>> UpdateDomain(int serverId, int domainId, [FromBody] UpdateMailDomainRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Write, ct))
            return Forbid();
        var domain = await mailService.UpdateDomainAsync(serverId, domainId, request, ct);
        return Ok(domain);
    }

    [HttpDelete("domains/{domainId:int}")]
    public async Task<IActionResult> DeleteDomain(int serverId, int domainId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Write, ct))
            return Forbid();
        await mailService.DeleteDomainAsync(serverId, domainId, ct);
        return NoContent();
    }

    [HttpGet("accounts")]
    public async Task<ActionResult<PaginatedResult<MailAccountDto>>> GetAccounts(
        int serverId, [FromQuery] PaginationRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Read, ct))
            return Forbid();
        var accounts = await mailService.GetAccountsAsync(serverId, request, ct);
        return Ok(accounts);
    }

    [HttpPost("accounts")]
    public async Task<ActionResult<MailAccountDto>> CreateAccount(int serverId, [FromBody] CreateMailAccountRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Write, ct))
            return Forbid();
        var account = await mailService.CreateAccountAsync(serverId, request, ct);
        return Created(string.Empty, account);
    }

    [HttpPut("accounts/{accountId:int}")]
    public async Task<ActionResult<MailAccountDto>> UpdateAccount(int serverId, int accountId, [FromBody] UpdateMailAccountRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Write, ct))
            return Forbid();
        var account = await mailService.UpdateAccountAsync(serverId, accountId, request, ct);
        return Ok(account);
    }

    [HttpDelete("accounts/{accountId:int}")]
    public async Task<IActionResult> DeleteAccount(int serverId, int accountId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Write, ct))
            return Forbid();
        await mailService.DeleteAccountAsync(serverId, accountId, ct);
        return NoContent();
    }

    [HttpPost("action")]
    public async Task<ActionResult<MailTaskQueuedDto>> ExecuteAction(int serverId, [FromBody] MailActionRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Write, ct))
            return Forbid();
        return Ok(await mailService.ExecuteActionAsync(serverId, request, ct));
    }

    [HttpPost("logs")]
    public async Task<ActionResult<MailTaskQueuedDto>> GetLogs(int serverId, [FromBody] MailLogRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Read, ct))
            return Forbid();
        return Ok(await mailService.GetLogsAsync(serverId, request, ct));
    }

    [HttpPost("setup")]
    public async Task<ActionResult<MailTaskQueuedDto>> Setup(int serverId, [FromBody] MailSetupRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Write, ct))
            return Forbid();
        return Ok(await mailService.SetupAsync(serverId, request, ct));
    }

    [HttpGet("domains/{domainId:int}/dns")]
    public async Task<ActionResult<MailDnsRecordsDto>> GetDnsRecords(int serverId, int domainId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Read, ct))
            return Forbid();
        var records = await mailService.GetDnsRecordsAsync(serverId, domainId, ct);
        return Ok(records);
    }

    // --- Aliases ---

    [HttpGet("aliases")]
    public async Task<ActionResult<PaginatedResult<MailAliasDto>>> GetAliases(
        int serverId, [FromQuery] PaginationRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Read, ct))
            return Forbid();
        var aliases = await mailService.GetAliasesAsync(serverId, request, ct);
        return Ok(aliases);
    }

    [HttpPost("domains/{domainId:int}/aliases")]
    public async Task<ActionResult<MailAliasDto>> CreateAlias(int serverId, int domainId, [FromBody] CreateMailAliasRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Write, ct))
            return Forbid();
        var alias = await mailService.CreateAliasAsync(serverId, domainId, request, ct);
        return Created(string.Empty, alias);
    }

    [HttpPut("aliases/{aliasId:int}")]
    public async Task<ActionResult<MailAliasDto>> UpdateAlias(int serverId, int aliasId, [FromBody] UpdateMailAliasRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Write, ct))
            return Forbid();
        var alias = await mailService.UpdateAliasAsync(serverId, aliasId, request, ct);
        return Ok(alias);
    }

    [HttpDelete("aliases/{aliasId:int}")]
    public async Task<IActionResult> DeleteAlias(int serverId, int aliasId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Write, ct))
            return Forbid();
        await mailService.DeleteAliasAsync(serverId, aliasId, ct);
        return NoContent();
    }

    // --- DKIM key rotation ---

    [HttpPost("domains/{domainId:int}/dkim/rotate")]
    public async Task<ActionResult<DkimRotationResultDto>> RotateDkimKey(int serverId, int domainId, [FromBody] DkimRotationRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Write, ct))
            return Forbid();
        var result = await mailService.RotateDkimKeyAsync(serverId, domainId, request, ct);
        return Ok(result);
    }
}
