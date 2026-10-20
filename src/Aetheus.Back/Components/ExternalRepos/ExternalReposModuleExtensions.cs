// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Configuration;

namespace Aetheus.Back.Components.ExternalRepos;

/// <summary>Registers the mirror-backed external repository services. A project uses an external
/// repository only when one is attached.</summary>
public static class ExternalReposModuleExtensions
{
    public static IServiceCollection AddExternalReposModule(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IExternalRepoService, ExternalRepoService>();
        services.AddScoped<IRepoSourceResolver, RepoSourceResolver>();
        services.AddHostedService<ExternalRepoSyncService>();

        return services;
    }
}
