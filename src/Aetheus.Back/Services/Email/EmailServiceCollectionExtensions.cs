// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Services.Email;

public static class EmailServiceCollectionExtensions
{
    /// <summary>Recette R1-16: the platform e-mail sender, shared by notifications and every later mail feature.</summary>
    public static IServiceCollection AddEmailSending(this IServiceCollection services)
    {
        services.AddScoped<IEmailSenderSettingsRepository, EmailSenderSettingsRepository>();
        services.AddScoped<IEmailSenderConfigurationResolver, EmailSenderConfigurationResolver>();
        services.AddSingleton<ISmtpTransport, MailKitSmtpTransport>();
        services.AddScoped<IEmailSender, EmailSender>();
        return services;
    }
}
