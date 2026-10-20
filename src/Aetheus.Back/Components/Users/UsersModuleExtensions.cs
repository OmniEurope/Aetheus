// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Users;

public static class UsersModuleExtensions
{
    public static IServiceCollection AddUsersModule(this IServiceCollection services)
    {
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IUserPreferenceRepository, UserPreferenceRepository>();
        services.AddScoped<IUserPreferenceService, UserPreferenceService>();
        return services;
    }
}
