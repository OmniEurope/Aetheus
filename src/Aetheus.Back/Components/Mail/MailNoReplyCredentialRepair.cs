// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Services.Email;

namespace Aetheus.Back.Components.Mail;

/// <summary>Re-provisions the no-reply password through <see cref="IMailNoReplySenderService"/> after the mail
/// server refused it. Runs at most once per <see cref="Cooldown"/>: the password task needs a few seconds to
/// reach the server, and a refusal with another cause (a mail stack that is down) must not rotate the
/// password again on every e-mail.</summary>
public sealed class MailNoReplyCredentialRepair(
    IServiceProvider services,
    NoReplyRepairThrottle throttle,
    ILogger<MailNoReplyCredentialRepair> logger) : INoReplyCredentialRepair
{
    internal static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(15);

    public async Task<bool> TryRepairAsync(CancellationToken ct = default)
    {
        if (!throttle.TryAcquire(Cooldown))
            return false;

        // Resolved here, not injected: the mail service sits behind the task service, which can itself
        // send e-mails, so injecting it would make the sender depend on itself.
        var prepare = services.GetRequiredService<IMailNoReplySenderService>();
        try
        {
            await prepare.PrepareAsync(ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is BadRequestException or ConflictException or NotFoundException)
        {
            // Mandatory: the request was refused (no agent capability, setup running...). The e-mail that met
            // the refusal is already reported as failed; the reason is kept in the log.
            logger.LogWarning(exception, "[Email] the no-reply password could not be re-provisioned");
            return false;
        }

        logger.LogWarning("[Email] the mail server refused the no-reply password: a new one was generated and queued for the mail server");
        return true;
    }
}
