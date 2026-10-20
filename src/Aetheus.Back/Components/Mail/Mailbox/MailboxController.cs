// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Microsoft.AspNetCore.RateLimiting;

namespace Aetheus.Back.Components.Mail.Mailbox;

/// <summary> The integrated mailbox of a mail account of the server. Rights follow the mail page:
/// reading needs Read on the server, changing the mailbox (send, delete, mark, create a folder)
/// needs Write. On top of that, nothing is read without the mailbox password the signed-in user
/// typed. </summary>
[ApiController]
[Route("api/servers/{serverId:int}/mail")]
[Authorize]
[ServiceFilter(typeof(ValidateServerExistsFilter))]
public class MailboxController(
    IMailboxService mailbox, IMailboxAdministratorAccess administratorAccess, IResourceAuthorizationService authz) : ControllerBase
{
    private const string Base = "accounts/{accountId:int}/mailbox";
    private const string AdministratorRole = "Admin";

    [HttpGet("client-settings")]
    public async Task<ActionResult<MailClientSettingsDto>> GetClientSettings(int serverId, CancellationToken ct)
    {
        if (!await CanAsync(serverId, Permission.Read, ct)) return Forbid();
        return Ok(await mailbox.GetClientSettingsAsync(serverId, ct));
    }

    [HttpGet(Base)]
    public async Task<ActionResult<MailboxAccessDto>> GetAccess(int serverId, int accountId, CancellationToken ct)
    {
        if (await KeyAsync(serverId, accountId, Permission.Read, ct) is not { } key) return Forbid();
        var access = await mailbox.GetAccessAsync(key, ct);
        // Recette R1-41: only an administrator is offered the opening without password.
        if (User.IsInRole(AdministratorRole))
            access = access with { CanOpenAsAdministrator = await administratorAccess.IsPreparedAsync(serverId, ct) };
        return Ok(access);
    }

    /// <summary>
    /// Recette R1-41 / R3-50: an administrator opens the mailbox without its password, through the server's
    /// Dovecot master user; the opening is written to the audit log. Anyone else keeps the password dialog.
    /// </summary>
    [HttpPost(Base + "/open-as-administrator")]
    [EnableRateLimiting("auth-token")]
    public async Task<ActionResult<MailboxAccessDto>> OpenAsAdministrator(int serverId, int accountId, CancellationToken ct)
    {
        if (!User.IsInRole(AdministratorRole)) return Forbid();
        if (await KeyAsync(serverId, accountId, Permission.Read, ct) is not { } key) return Forbid();
        return Ok(await administratorAccess.OpenAsync(key, isAdministrator: true, ct));
    }

    [HttpPost(Base + "/unlock")]
    [EnableRateLimiting("auth-token")]
    public async Task<ActionResult<MailboxAccessDto>> Unlock(
        int serverId, int accountId, [FromBody] MailboxUnlockRequest request, CancellationToken ct)
    {
        if (await KeyAsync(serverId, accountId, Permission.Read, ct) is not { } key) return Forbid();
        return Ok(await mailbox.UnlockAsync(key, request.Password, ct));
    }

    [HttpPost(Base + "/lock")]
    public async Task<IActionResult> Lock(int serverId, int accountId, CancellationToken ct)
    {
        if (await KeyAsync(serverId, accountId, Permission.Read, ct) is not { } key) return Forbid();
        mailbox.Lock(key);
        return NoContent();
    }

    [HttpGet(Base + "/folders")]
    public async Task<ActionResult<List<MailboxFolderDto>>> GetFolders(int serverId, int accountId, CancellationToken ct)
    {
        if (await KeyAsync(serverId, accountId, Permission.Read, ct) is not { } key) return Forbid();
        return Ok(await mailbox.ListFoldersAsync(key, ct));
    }

    [HttpPost(Base + "/folders")]
    public async Task<ActionResult<MailboxFolderDto>> CreateFolder(
        int serverId, int accountId, [FromBody] MailboxCreateFolderRequest request, CancellationToken ct)
    {
        if (await KeyAsync(serverId, accountId, Permission.Write, ct) is not { } key) return Forbid();
        return Ok(await mailbox.CreateFolderAsync(key, request.Name, ct));
    }

    [HttpGet(Base + "/messages")]
    public async Task<ActionResult<PaginatedResult<MailboxMessageSummaryDto>>> GetMessages(
        int serverId, int accountId, [FromQuery] string folder, [FromQuery] int page = 1, [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        if (await KeyAsync(serverId, accountId, Permission.Read, ct) is not { } key) return Forbid();
        return Ok(await mailbox.ListMessagesAsync(key, folder, page, pageSize, ct));
    }

    [HttpGet(Base + "/messages/{uid:long}")]
    public async Task<ActionResult<MailboxMessageDto>> GetMessage(
        int serverId, int accountId, uint uid, [FromQuery] string folder, [FromQuery] bool loadImages = false,
        CancellationToken ct = default)
    {
        if (await KeyAsync(serverId, accountId, Permission.Read, ct) is not { } key) return Forbid();
        return Ok(await mailbox.GetMessageAsync(key, folder, uid, loadImages, ct));
    }

    /// <summary>The attachment's bytes, always as a download (octet-stream): never rendered by the browser.</summary>
    [HttpGet(Base + "/messages/{uid:long}/attachments/{index:int}")]
    public async Task<IActionResult> GetAttachment(
        int serverId, int accountId, uint uid, int index, [FromQuery] string folder, CancellationToken ct)
    {
        if (await KeyAsync(serverId, accountId, Permission.Read, ct) is not { } key) return Forbid();
        var attachment = await mailbox.GetAttachmentAsync(key, folder, uid, index, ct);
        return File(attachment.Content, "application/octet-stream", attachment.FileName);
    }

    [HttpGet(Base + "/messages/{uid:long}/draft")]
    public async Task<ActionResult<MailboxDraftDto>> GetDraft(
        int serverId, int accountId, uint uid, [FromQuery] string folder, [FromQuery] MailboxComposeMode mode,
        [FromQuery] string? language, CancellationToken ct)
    {
        if (await KeyAsync(serverId, accountId, Permission.Read, ct) is not { } key) return Forbid();
        return Ok(await mailbox.GetDraftAsync(key, mode, folder, uid, language, ct));
    }

    [HttpPut(Base + "/messages/{uid:long}/read")]
    public async Task<IActionResult> SetRead(
        int serverId, int accountId, uint uid, [FromQuery] string folder, [FromBody] MailboxReadStateRequest request,
        CancellationToken ct)
    {
        if (await KeyAsync(serverId, accountId, Permission.Write, ct) is not { } key) return Forbid();
        await mailbox.SetReadAsync(key, folder, uid, request.IsRead, ct);
        return NoContent();
    }

    [HttpDelete(Base + "/messages/{uid:long}")]
    public async Task<ActionResult<MailboxDeleteResultDto>> Delete(
        int serverId, int accountId, uint uid, [FromQuery] string folder, CancellationToken ct)
    {
        if (await KeyAsync(serverId, accountId, Permission.Write, ct) is not { } key) return Forbid();
        return Ok(await mailbox.DeleteAsync(key, folder, uid, ct));
    }

    /// <summary> The attached files travel as base64 in the body, so the send reads up to <see
    /// cref="MailboxLimits.MaxSendRequestBytes"/> instead of the 10 MB default; the service checks
    /// the count and sizes of the files themselves. </summary>
    [HttpPost(Base + "/send")]
    [RequestSizeLimit(MailboxLimits.MaxSendRequestBytes)]
    public async Task<ActionResult<MailboxSendResultDto>> Send(
        int serverId, int accountId, [FromBody] MailboxSendRequest request, CancellationToken ct)
    {
        if (await KeyAsync(serverId, accountId, Permission.Write, ct) is not { } key) return Forbid();
        return Ok(await mailbox.SendAsync(key, request, ct));
    }

    private Task<bool> CanAsync(int serverId, Permission permission, CancellationToken ct) =>
        authz.HasPermissionAsync(User, ResourceType.Server, serverId, permission, ct);

    /// <summary>
    /// The held password's key for the signed-in user, or null when the user lacks the right or carries no
    /// identifier (fail closed: an anonymous key would share one password between callers).
    /// </summary>
    private async Task<MailboxCredentialKey?> KeyAsync(int serverId, int accountId, Permission permission, CancellationToken ct)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId) || !await CanAsync(serverId, permission, ct)) return null;
        // The scheme is part of the key: an agent principal carries its server id as identifier.
        return new MailboxCredentialKey(MailboxCredentialKey.Holder(User.Identity?.AuthenticationType, userId), serverId, accountId);
    }
}
