// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Git;

public static class GitModuleExtensions
{
    public static IServiceCollection AddGitModule(this IServiceCollection services, IConfiguration configuration)
    {
        // Existing Git connections
        services.AddScoped<IGitRepository, GitRepository>();
        services.AddScoped<IGitService, GitService>();

        // Git Light (internal repos)
        services.Configure<GitLightOptions>(configuration.GetSection("GitLight"));
        services.AddScoped<IGitLightRepository, GitLightRepository>();
        // Mirrors of external repositories: the credentialed fetch and its on-demand refresh.
        services.AddSingleton<CreditedGitRunner>();
        services.AddSingleton(sp => new ExternalGitEndpointGuard(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<GitLightOptions>>().Value));
        services.AddScoped<IExternalRepoMirrorService, ExternalRepoMirrorService>();
        services.AddScoped<IExternalMirrorRefresher, ExternalMirrorRefresher>();
        services.AddScoped<GitProcessRunner>();
        services.AddScoped<GitLightCliWriter>();
        services.AddScoped<IGitLightCliService, GitLightCliService>();
        services.AddScoped<GitBranchProtectionService>();
        services.AddScoped<IGitLightService, GitLightService>();
        services.AddScoped<IGitDevelopBranchProvisioner, GitDevelopBranchProvisioner>();
        services.AddScoped<IGitCommitRecorder, GitCommitRecorder>();
        // The push that creates a repository's default branch is its first commit (R-76).
        services.AddScoped<
            Services.DomainEvents.IDomainEventHandler<Events.GitPushProcessedEvent>,
            GitDevelopBranchPushHandler>();
        services.AddScoped<GitFilterValuesService>();
        services.AddScoped<GitArchiveService>();
        services.AddScoped<IGitSmartHttpService, GitSmartHttpService>();
        services.AddScoped<IGitBranchMovedAnnouncer, GitBranchMovedAnnouncer>();
        services.AddScoped<IGitBranchAdvanceService, GitBranchAdvanceService>();
        services.AddHostedService<GitLightMaintenanceService>();

        return services;
    }
}
