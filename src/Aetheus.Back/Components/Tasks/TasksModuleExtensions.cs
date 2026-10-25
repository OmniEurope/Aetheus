// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Tasks;

public static class TasksModuleExtensions
{
    public static IServiceCollection AddTasksModule(this IServiceCollection services)
    {
        services.AddScoped<ITaskRepository, TaskRepository>();
        services.AddScoped<IPipelineTaskLifecycle, PipelineTaskLifecycleRepository>();
        services.AddScoped<ITaskQueueNotifier, TaskQueueNotifier>();
        services.AddScoped<ITaskService, TaskService>();
        services.AddScoped<ITaskServerReader>(sp => sp.GetRequiredService<ITaskService>());
        services.AddScoped<IUnexecutableTaskRefuser, UnexecutableTaskRefuser>();
        services.AddScoped<Services.DomainEvents.IDomainEventHandler<ReleaseDeployedEvent>, ReleaseDeployedNotificationHandler>();
        return services;
    }
}
