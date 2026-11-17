// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Services;
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.Shared;

public static class SharedModuleExtensions
{
    /// <summary>
    /// Registers cross-module filters and helpers used by multiple feature modules
    /// (Apache, Certbot, Docker, Mail, Cron, Teamspeak, Portsentry, Rkhunter, ...).
    /// Should be called once from <c>Program.cs</c> instead of being duplicated in each
    /// module's <c>Add{Module}Module()</c>.
    /// </summary>
    public static IServiceCollection AddSharedModule(this IServiceCollection services)
    {
        services.AddScoped<ServerExistenceRepository>();
        services.AddScoped<ValidateServerExistsFilter>();
        services.AddSingleton<IBackgroundTaskQueue, BackgroundTaskQueue>();
        services.AddHostedService<BackgroundTaskQueueHostedService>();
        services.AddScoped<IDomainEventDispatcher, DomainEventDispatcher>();

        // Leader election for every hosted worker (ADR-029). Registered here, once, because dozens of workers of
        // every module depend on it: a worker that cannot get an IPostgresLeaderLease fails to build, it never
        // runs without election. Only the colour behind the public API address leads (decision of 2026-10-02):
        // the lease asks the probe, which asks the address itself which instance answers.
        services.AddHttpClient(LiveInstanceProbe.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(5));
        services.AddSingleton<LiveInstanceProbe>();
        services.AddSingleton<ILiveInstanceProbe>(provider => provider.GetRequiredService<LiveInstanceProbe>());
        services.AddHostedService(provider => provider.GetRequiredService<LiveInstanceProbe>());
        services.AddSingleton<IPostgresLeaderLease, PostgresLeaderLease>();

        // The audit observers of each module's events are registered by that module (Pipelines, Servers):
        // this one sits below every module and names none of them (layer guard, 2026-09-25).
        return services;
    }
}
