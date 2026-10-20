// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.Servers;

public static class ServersModuleExtensions
{
    public static IServiceCollection AddServersModule(this IServiceCollection services)
    {
        services.AddScoped<IServerRepository, ServerRepository>();
        // Same scope as IServerRepository on purpose: both resolve the SAME AppDbContext, which is
        // what keeps one heartbeat one transaction after the split.
        services.AddScoped<IServerHeartbeatRepository, ServerHeartbeatRepository>();
        services.AddScoped<IServerRetirementRepository, ServerRetirementRepository>();
        services.AddScoped<IServerRetirementService, ServerRetirementService>();
        services.AddScoped<ServerService>();
        // Segregated facets resolve to the same instance - consumers depend on the narrowest surface.
        services.AddScoped<IServerLifecycleService>(sp => sp.GetRequiredService<ServerService>());
        services.AddScoped<IServerHeartbeatService>(sp => sp.GetRequiredService<ServerService>());
        services.AddScoped<IServerServiceManagementService>(sp => sp.GetRequiredService<ServerService>());
        services.AddScoped<IServerAgentContactService>(sp => sp.GetRequiredService<ServerService>());
        services.AddScoped<IServerDiagnosticService>(sp => sp.GetRequiredService<ServerService>());
        // PLAN-010 lot 1: external TCP probe run by the timeout sweep when an agent goes silent.
        services.AddOptions<ServerReachabilityProbeOptions>().BindConfiguration(ServerReachabilityProbeOptions.SectionName);
        services.AddSingleton<ITcpReachabilityProber, TcpReachabilityProber>();
        services.AddScoped<IServerReachabilityRepository, ServerReachabilityRepository>();
        services.AddScoped<ServerReachabilityProbe>();
        services.AddHostedService<ServerTimeoutService>();
        // Audit observer and admin toast of this module's ServerWentOffline event (moved from the Shared
        // module, 2026-09-25).
        services.AddScoped<IDomainEventHandler<Events.ServerWentOfflineEvent>, DomainEventAuditHandler<Events.ServerWentOfflineEvent>>();
        services.AddScoped<IDomainEventHandler<Events.ServerWentOfflineEvent>, Handlers.ServerOfflineNotificationHandler>();
        // Persist sudoers drift in the audit trail and administrator notifications so it remains
        // visible beyond connected browser sessions.
        services.AddScoped<IDomainEventHandler<Events.SudoersDriftDetectedEvent>, DomainEventAuditHandler<Events.SudoersDriftDetectedEvent>>();
        services.AddScoped<IDomainEventHandler<Events.SudoersDriftDetectedEvent>, Handlers.SudoersDriftNotificationHandler>();
        // PLAN-010 lot 1: an OOM kill is kept in the audit trail and the administrators' notifications.
        services.AddScoped<IDomainEventHandler<Events.OomKillDetectedEvent>, DomainEventAuditHandler<Events.OomKillDetectedEvent>>();
        services.AddScoped<IDomainEventHandler<Events.OomKillDetectedEvent>, Handlers.OomKillNotificationHandler>();
        // Same durable trail for containers with host-escape settings (PLAN-010 lot 1).
        services.AddScoped<IDomainEventHandler<Events.DockerContainerRiskDetectedEvent>, DomainEventAuditHandler<Events.DockerContainerRiskDetectedEvent>>();
        services.AddScoped<IDomainEventHandler<Events.DockerContainerRiskDetectedEvent>, Handlers.DockerContainerRiskNotificationHandler>();
        services.AddScoped<IDomainEventHandler<Events.SshConfigChangedEvent>, DomainEventAuditHandler<Events.SshConfigChangedEvent>>();
        services.AddScoped<IDomainEventHandler<Events.SshConfigChangedEvent>, Handlers.SshConfigChangeNotificationHandler>();
        return services;
    }
}
