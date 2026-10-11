// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Notifications;

public static class NotificationsModuleExtensions
{
    public static IServiceCollection AddNotificationsModule(this IServiceCollection services)
    {
        services.AddScoped<INotificationRepository, NotificationRepository>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<IUserNotificationRepository, UserNotificationRepository>();
        services.AddScoped<IUserNotificationService, UserNotificationService>();
        services.AddScoped<INotificationEmailer, NotificationEmailer>();
        return services;
    }
}
