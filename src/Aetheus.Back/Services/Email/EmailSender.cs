// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Services.Email;

/// <summary> The one way Aetheus sends e-mail. The sender is resolved for every call (the settings
/// may have changed), the messages share one SMTP connection, and every outcome is reported:
/// nothing is ever called sent unless the server accepted it. </summary>
public sealed class EmailSender(
    IEmailSenderConfigurationResolver resolver,
    ISmtpTransport transport,
    ILogger<EmailSender> logger) : IEmailSender
{
    public async Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var results = await SendBatchAsync([message], ct).ConfigureAwait(false);
        return results[0];
    }

    public async Task<IReadOnlyList<EmailSendResult>> SendBatchAsync(
        IReadOnlyList<EmailMessage> messages, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count == 0) return [];

        var resolution = await resolver.ResolveAsync(ct).ConfigureAwait(false);
        if (resolution.Configuration is not { } configuration)
            return Repeat(EmailSendResult.NotConfigured(resolution.Describe()), messages.Count);

        ISmtpSession session;
        try
        {
            session = await transport.ConnectAsync(configuration, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsCallerCancellation(exception, ct))
        {
            // Mandatory: the SMTP text is the verdict the caller must show verbatim.
            logger.LogWarning(exception, "[Email] connection to {Host}:{Port} failed", configuration.Host, configuration.Port);
            return Repeat(EmailSendResult.Failed(exception.Message), messages.Count);
        }

        var results = new List<EmailSendResult>(messages.Count);
        await using (session.ConfigureAwait(false))
        {
            foreach (var message in messages)
                results.Add(await SendOneAsync(session, message, configuration, ct).ConfigureAwait(false));
        }
        return results;
    }

    private async Task<EmailSendResult> SendOneAsync(
        ISmtpSession session, EmailMessage message, EmailSenderConfiguration configuration, CancellationToken ct)
    {
        try
        {
            await session.SendAsync(message, ct).ConfigureAwait(false);
            return EmailSendResult.Sent(configuration.FromAddress);
        }
        catch (Exception exception) when (!IsCallerCancellation(exception, ct))
        {
            // Mandatory: one refused recipient must not stop the others, and its reason is kept as is.
            logger.LogWarning(exception, "[Email] message to {Recipient} was not accepted", message.To);
            return EmailSendResult.Failed(exception.Message);
        }
    }

    private static bool IsCallerCancellation(Exception exception, CancellationToken ct) =>
        exception is OperationCanceledException && ct.IsCancellationRequested;

    private static EmailSendResult[] Repeat(EmailSendResult result, int count) =>
        Enumerable.Repeat(result, count).ToArray();
}
