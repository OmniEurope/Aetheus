// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Notifications.MailTemplates;

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
        // Mail templates (recettes R1-74, R1-75): the channel test is written from one; the recipient style also gives notifications their language.
        services.AddScoped<IMailTemplateRepository, MailTemplateRepository>();
        services.AddScoped<IMailTemplateRenderer, MailTemplateRenderer>();
        services.AddScoped<IMailTemplateService, MailTemplateService>();
        return services;
    }
}
