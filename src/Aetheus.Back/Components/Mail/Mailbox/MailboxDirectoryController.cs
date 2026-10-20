// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Mail.Mailbox;

/// <summary>
/// Recette R1-41: the "Messagerie" page, the mailboxes of every server the reader can read (all of them
/// for an administrator), so a mailbox is found without going through Servers, the server and Mail.
/// </summary>
[ApiController]
[Route("api/mail/mailboxes")]
[Authorize]
public class MailboxDirectoryController(IMailboxService mailbox, IResourceAuthorizationService authz) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PaginatedResult<MailboxDirectoryEntryDto>>> List(
        [FromQuery] PaginationRequest request, CancellationToken ct)
    {
        var serverIds = await authz.GetAccessibleResourceIdsAsync(User, ResourceType.Server, Permission.Read, ct);
        return Ok(await mailbox.ListDirectoryAsync(serverIds, request, ct));
    }
}
