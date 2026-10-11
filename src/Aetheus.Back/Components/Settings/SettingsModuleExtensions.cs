// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Settings;

public static class SettingsModuleExtensions
{
    public static IServiceCollection AddSettingsModule(this IServiceCollection services)
    {
        services.AddScoped<ISettingsRepository, SettingsRepository>();
        services.AddScoped<ISettingsService, SettingsService>();
        services.AddScoped<IEmailSenderSettingsService, EmailSenderSettingsService>();
        return services;
    }
}
