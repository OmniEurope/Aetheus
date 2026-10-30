// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Auth;

public static class AuthModuleExtensions
{
    public static IServiceCollection AddAuthModule(this IServiceCollection services)
    {
        services.AddScoped<IAuthRepository, AuthRepository>();
        services.AddScoped<ITotpService, TotpService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IServerEnrollmentService, ServerEnrollmentService>();
        services.AddScoped<IRoleRepository, RoleRepository>();
        services.AddScoped<IRoleService, RoleService>();
        services.AddScoped<IPasswordSetupRepository, PasswordSetupRepository>();
        services.AddScoped<IPasswordSetupService, PasswordSetupService>();
        services.AddScoped<Services.DomainEvents.IDomainEventHandler<Users.Events.UserWelcomeRequestedEvent>, UserWelcomeMailHandler>();
        services.AddScoped<ISiteAdminBootstrapRepository, SiteAdminBootstrapRepository>();
        services.AddScoped<ISiteAdminGrantMail, SiteAdminGrantMail>();
        services.AddScoped<ISiteAdminBootstrap, SiteAdminBootstrap>();
        services.AddScoped<Services.DomainEvents.IDomainEventHandler<AccountAccessProvedEvent>, SiteAdminBootstrapHandler>();
        services.AddScoped<Services.DomainEvents.IDomainEventHandler<Users.Events.SiteAdminGrantedEvent>, SiteAdminGrantedMailHandler>();
        services.AddHostedService<RefreshTokenCleanupService>();
        return services;
    }
}
