// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Servers.Events;
using Aetheus.Back.Services.DomainEvents;
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

        // PLAN-005: inventory reconciliation, TLS / spam / delivery operations, DNS verification, diagnostics.
        services.AddScoped<IMailInventoryRepository, MailInventoryRepository>();
        services.AddScoped<IMailStateReconciler, MailStateReconciler>();
        services.AddScoped<IDomainEventHandler<MailInventoryReportedEvent>, MailInventoryReconciliationHandler>();
        services.AddScoped<IMailOperationsService, MailOperationsService>();
        services.AddScoped<IMailStackPurgeService, MailStackPurgeService>();
        services.AddSingleton<IMailDnsResolver, DnsClientMailResolver>();
        services.AddScoped<IMailDnsVerifier, MailDnsVerifier>();
        services.AddScoped<IMailDiagnosticsService, MailDiagnosticsService>();
        services.AddScoped<IMailNoReplySenderService, MailNoReplySenderService>();
        services.AddHostedService<MailDnsRefreshService>();

        // Recette R1-17: the integrated mailbox. The typed mailbox passwords live in one in-memory store.
        services.AddScoped<Mailbox.IMailboxRepository, Mailbox.MailboxRepository>();
        services.AddScoped<Mailbox.IMailboxService, Mailbox.MailboxService>();
        services.AddSingleton<Mailbox.IMailboxConnector, Mailbox.MailKitMailboxConnector>();
        services.AddSingleton<Mailbox.IMailboxCredentialStore, Mailbox.MailboxCredentialStore>();
        // Recette R1-37: a user's session end forgets the mailbox passwords they hold.
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
