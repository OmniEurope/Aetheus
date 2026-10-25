// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Vaults;

public static class VaultsModuleExtensions
{
    public static IServiceCollection AddVaultsModule(this IServiceCollection services)
    {
        services.AddScoped<IVaultRepository, VaultRepository>();
        services.AddScoped<IVaultService, VaultService>();
        return services;
    }
}
