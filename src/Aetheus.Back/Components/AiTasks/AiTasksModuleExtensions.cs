// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.AiTasks;

public static class AiTasksModuleExtensions
{
    /// <summary>Switch of the nightly model list read (recette R3-7). On by default, so production
    /// keeps it; the integration and E2E hosts (ylaunch, ybaunch, the QA stack) set it to false: the
    /// nightly QA runs inside the read window, and a seeded profile on a provider's default URL would
    /// send its fake key to the real provider.</summary>
    public const string ModelListRefreshEnabledKey = "AiTasks:ModelListRefresh:Enabled";

    public static IServiceCollection AddAiTasksModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddScoped<IAiTaskRepository, AiTaskRepository>();
        // Reactive: Notifications states that a product event happened, this module decides whether it
        // starts an AI task. Observer dispatch, so a trigger failure cannot stop a notification.
        services.AddScoped<
            Services.DomainEvents.IDomainEventHandler<Notifications.Events.NotificationEventRaisedEvent>,
            NotificationAiTaskTriggerHandler>();
        services.AddScoped<AiTaskService>();
        services.AddScoped<IAiTaskService>(provider => provider.GetRequiredService<AiTaskService>());
        services.AddScoped<IAiRunnerProfileService, AiRunnerProfileService>();
        services.AddHostedService<AiTaskSchedulerService>();
        // One reader for the nightly pass and the dialog's "read now": same request, storage and errors.
        services.AddSingleton<AiModelListReader>();
        if (configuration.GetValue(ModelListRefreshEnabledKey, true))
            services.AddHostedService<AiModelListRefreshService>();

        // "Test the connection" of an AI profile and the model list (recette R3-7), both sent by the
        // backend: no redirects (the request carries the API key) and the SSRF guard of the other
        // outbound probes, so an admin-typed base URL cannot reach an internal service.
        // AiTasks:AllowPrivateTestTargets opens it for a model served on the operator network; runs
        // themselves are sent by the agent, not from here.
        foreach (var clientName in new[] { AiRunnerProfileService.TestHttpClientName, AiModelListReader.HttpClientName })
        {
            services.AddHttpClient(clientName, client =>
            {
                client.Timeout = Timeout.InfiniteTimeSpan; // bounded per call by AiProviderClient
                client.MaxResponseContentBufferSize = 1024 * 1024;
            }).ConfigurePrimaryHttpMessageHandler(provider => Shared.SsrfProtectedHttpHandlerFactory.Create(
                provider.GetRequiredService<IConfiguration>().GetValue("AiTasks:AllowPrivateTestTargets", false),
                "AI provider target resolved to a forbidden address."));
        }
        return services;
    }
}
