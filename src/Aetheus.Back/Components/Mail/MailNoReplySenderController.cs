// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Mail;

/// <summary>The platform settings' action that prepares the default sender's mailbox.</summary>
[ApiController]
[Route("api/mail/no-reply-sender")]
[Authorize(Roles = "Admin")]
public class MailNoReplySenderController(IMailNoReplySenderService service) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<EmailSenderSettingsDto>> Prepare(CancellationToken ct) =>
        Ok(await service.PrepareAsync(ct));
}
