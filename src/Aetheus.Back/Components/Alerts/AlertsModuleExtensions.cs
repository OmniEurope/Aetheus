// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Alerts;

public static class AlertsModuleExtensions
{
    public static IServiceCollection AddAlertsModule(this IServiceCollection services)
    {
        services.AddScoped<IAlertRepository, AlertRepository>();
        services.AddScoped<IAlertService, AlertService>();
        services.AddScoped<IAlertOccurrenceRepository, AlertOccurrenceRepository>();
        services.AddScoped<IAlertOccurrenceService, AlertOccurrenceService>();
        services.AddHostedService<AlertEvaluatorService>();
        services.AddHostedService<AlertOccurrenceRetentionService>();
        services.AddHostedService<StorageAlertProvisioningService>();
        services.AddHostedService<SecretExpirationService>();
        return services;
    }
}
