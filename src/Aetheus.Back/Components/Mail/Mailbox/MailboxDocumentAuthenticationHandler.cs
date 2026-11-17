// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Aetheus.Back.Components.Mail.Mailbox;

/// <summary>
/// Authenticates the reading frame's request by its message ticket (<see cref="IMailboxDocumentTickets"/>):
/// the frame cannot send the JWT, so the ticket, issued by the authorized read of the message, is the
/// credential. It is accepted only while it is live and while the reader still holds the mailbox password
/// (a lock, the password's expiry or the end of the reader's session refuse it). The ticket is consumed by
/// <see cref="MailboxDocumentController"/>, not here, so it stays single-use. Activated through
/// <c>[Authorize(AuthenticationSchemes = MailboxDocumentAuthenticationHandler.SchemeName)]</c>.
/// </summary>
public sealed class MailboxDocumentAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IMailboxDocumentTickets tickets,
    IMailboxCredentialStore credentials)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "MailboxDocument";

    /// <summary>The route value holding the ticket.</summary>
    public const string TicketRouteValue = "ticket";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.RouteValues[TicketRouteValue] is not string ticket || ticket.Length == 0)
            return Task.FromResult(AuthenticateResult.NoResult());
        if (tickets.Peek(ticket) is not { } live)
            return Task.FromResult(AuthenticateResult.Fail("Unknown, used or expired message ticket."));
        if (credentials.ExpiresAt(live.Key) is null)
            return Task.FromResult(AuthenticateResult.Fail("The mailbox password is no longer held."));

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, live.Key.UserId)], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
