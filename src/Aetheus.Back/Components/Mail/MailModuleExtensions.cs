// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Mail.Mailbox;
using Aetheus.Back.Components.Servers.Events;
using Aetheus.Back.Services.DomainEvents;
using Aetheus.Back.Services.Email;
using Microsoft.Extensions.Caching.Memory;

namespace Aetheus.Back.Components.Mail;

public static class MailModuleExtensions
{
    public static IServiceCollection AddMailModule(this IServiceCollection services)
    {
        services.AddScoped<IMailRepository, MailRepository>();
        services.AddKeyedScoped<IMailService, MailService>("inner");
        services.AddScoped<IMailService>(sp =>
            new CachedMailService(
                sp.GetRequiredKeyedService<IMailService>("inner"),
                sp.GetRequiredService<IMemoryCache>()));

        // Mail inventory reconciliation, operations, DNS verification and diagnostics.
        services.AddScoped<IMailInventoryRepository, MailInventoryRepository>();
        services.AddScoped<IMailStateReconciler, MailStateReconciler>();
        services.AddScoped<IMailQuotaResync, MailQuotaResync>();
        services.AddScoped<IDomainEventHandler<MailInventoryReportedEvent>, MailInventoryReconciliationHandler>();
        services.AddScoped<IMailOperationsService, MailOperationsService>();
        services.AddScoped<IMailStackPurgeService, MailStackPurgeService>();
        services.AddSingleton<IMailDnsResolver, DnsClientMailResolver>();
        services.AddScoped<IMailDnsVerifier, MailDnsVerifier>();
        services.AddScoped<IMailDiagnosticsService, MailDiagnosticsService>();
        services.AddScoped<IMailNoReplySenderService, MailNoReplySenderService>();
        // The default sender's secret follows every password set on the no-reply mailbox, and the sender
        // re-provisions that password when the mail server refuses it (SMTP 535).
        services.AddScoped<IMailSenderSecret, MailSenderSecret>();
        services.AddSingleton<NoReplyRepairThrottle>();
        services.AddScoped<INoReplyCredentialRepair, MailNoReplyCredentialRepair>();
        // Recette R1-41 / R3-50 / R3-49: the Dovecot master user, its secret, and the purge of absent mailbox rows.
        services.AddScoped<IMailMasterSecrets, MailMasterSecrets>();
        services.AddScoped<IMailMasterAccessService, MailMasterAccessService>();
        services.AddScoped<IMailAccountPurgeService, MailAccountPurgeService>();
        services.AddScoped<Mailbox.IMailboxAdministratorAccess, Mailbox.MailboxAdministratorAccess>();
        services.AddHostedService<MailDnsRefreshService>();

        // The integrated mailbox. The typed mailbox passwords live in one in-memory store.
        services.AddScoped<Mailbox.IMailboxRepository, Mailbox.MailboxRepository>();
        services.AddScoped<Mailbox.IMailboxService, Mailbox.MailboxService>();
        services.AddSingleton<Mailbox.IMailboxConnector, Mailbox.MailKitMailboxConnector>();
        services.AddSingleton<Mailbox.IMailboxCredentialStore, Mailbox.MailboxCredentialStore>();
        // A user's session end forgets the mailbox passwords they hold.
        services.AddScoped<IDomainEventHandler<Users.Events.UserSessionEndedEvent>, Mailbox.MailboxCredentialSessionEndHandler>();
        services.AddScoped<Mailbox.IMailboxRemoteImageFetcher, Mailbox.MailboxRemoteImageFetcher>();
        // "Show images": the shared SSRF guard (no private or loopback target, no redirect, no cookie).
        services.AddHttpClient(Mailbox.MailboxRemoteImageFetcher.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
            client.MaxResponseContentBufferSize = Mailbox.MailboxRemoteImageFetcher.MaxImageBytes;
        }).ConfigurePrimaryHttpMessageHandler(() => SsrfProtectedHttpHandlerFactory.Create(
            allowPrivate: false, "The image address resolved to a forbidden (private or loopback) address."));
        return services;
    }
}
