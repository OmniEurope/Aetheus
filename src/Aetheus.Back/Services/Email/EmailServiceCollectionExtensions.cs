// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Services.Email;

public static class EmailServiceCollectionExtensions
{
    /// <summary>Registers the platform email sender shared by email features.</summary>
    public static IServiceCollection AddEmailSending(this IServiceCollection services)
    {
        services.AddScoped<IEmailSenderSettingsRepository, EmailSenderSettingsRepository>();
        services.AddScoped<IEmailSenderConfigurationResolver, EmailSenderConfigurationResolver>();
        services.AddSingleton<ISmtpTransport, MailKitSmtpTransport>();
        services.AddScoped<IEmailSender, EmailSender>();
        return services;
    }
}
