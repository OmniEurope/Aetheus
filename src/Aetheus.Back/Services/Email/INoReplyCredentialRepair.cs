// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Services.Email;

/// <summary>Brings the password Aetheus holds for the managed no-reply mailbox and the password the mail
/// server holds back in line. Called by <see cref="EmailSender"/> when the server refuses the managed
/// sender's credentials (SMTP 535): the two copies of the password can only differ by a failure on one side
/// (a password task that never ran, a mailbox recreated by a mail reinstall, a password changed by hand).</summary>
public interface INoReplyCredentialRepair
{
    /// <summary>Queues a new generated password for the no-reply mailbox through the agent, at most once per
    /// cool-down period. False when nothing was queued (cool-down running, or the request was refused).</summary>
    Task<bool> TryRepairAsync(CancellationToken ct = default);
}
