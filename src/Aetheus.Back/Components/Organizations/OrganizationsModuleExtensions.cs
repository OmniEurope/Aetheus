// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Organizations;

public static class OrganizationsModuleExtensions
{
    public static IServiceCollection AddOrganizationsModule(this IServiceCollection services)
    {
        services.AddScoped<IOrganizationRepository, OrganizationRepository>();
        services.AddScoped<IOrganizationService, OrganizationService>();
        services.AddScoped<IOrganizationRoleAssignmentRepository, OrganizationRoleAssignmentRepository>();
        services.AddScoped<IOrganizationRoleAssignmentService, OrganizationRoleAssignmentService>();
        return services;
    }
}
