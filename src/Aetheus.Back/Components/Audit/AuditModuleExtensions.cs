// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Audit;

public static class AuditModuleExtensions
{
    public static IServiceCollection AddAuditModule(this IServiceCollection services)
    {
        services.AddScoped<IAuditRepository, AuditRepository>();
        services.AddScoped<IAuditChainService, AuditChainService>();
        services.AddScoped<IAuditService, AuditService>();
        // R-7: the person a scope acts for once its work left their request (the deferred first dispatch).
        services.AddScoped<IAuditActor, AuditActor>();
        return services;
    }
}
