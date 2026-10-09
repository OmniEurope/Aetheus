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
        services.AddHostedService<MailDnsRefreshService>();
        return services;
    }
}
