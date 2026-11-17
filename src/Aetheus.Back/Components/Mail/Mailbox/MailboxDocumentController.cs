// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Mail.Mailbox;

/// <summary>
/// The sanitized document of one message, loaded by the reading frame by URL, with its own
/// Content-Security-Policy (<see cref="MailboxDocumentPolicy"/>) in place of the interface's. The request is
/// authenticated by the single-use ticket the authorized read of the message issued
/// (<see cref="MailboxDocumentAuthenticationHandler"/>); the ticket is consumed here.
/// </summary>
[ApiController]
[Route("api/mail/documents")]
[Authorize(AuthenticationSchemes = MailboxDocumentAuthenticationHandler.SchemeName)]
public sealed class MailboxDocumentController(IMailboxDocumentTickets tickets, MailboxDocumentPolicy policy) : ControllerBase
{
    [HttpGet("{" + MailboxDocumentAuthenticationHandler.TicketRouteValue + "}")]
    public IActionResult Get(string ticket)
    {
        // A second request racing the first one past authentication finds the ticket gone.
        if (tickets.Redeem(ticket) is not { } redeemed) return NotFound();

        var headers = Response.Headers;
        // Replaces the API's own policy (set before the action by the security headers middleware) for
        // this response only; every other route keeps it unchanged.
        headers.ContentSecurityPolicy = policy.ContentSecurityPolicy;
        // frame-ancestors above says who may frame the document; the API-wide DENY would forbid every frame.
        headers.Remove("X-Frame-Options");
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers.CacheControl = "no-store";
        headers.Pragma = "no-cache";
        return Content(redeemed.Document, "text/html; charset=utf-8");
    }
}
