// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines.Events;
using Aetheus.Back.Components.VulnerabilityWatch;
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.Analysis.DependencyUpdates;

public static class DependencyUpdatesModuleExtensions
{
    /// <summary>
    /// PLAN-007: the automatic dependency updates. Registered right after the analysis module, whose publication and announcement
    /// handlers of each matching pass the update pass must follow; the module lives in its own namespace so that the analysis
    /// namespace never references it.
    /// </summary>
    public static IServiceCollection AddDependencyUpdatesModule(this IServiceCollection services)
    {
        services.AddScoped<DependencyUpdateRepository>();
        services.AddScoped<IDependencyUpdateGit, DependencyUpdateGit>();
        services.AddScoped<IDependencyUpdatePipelines, DependencyUpdatePipelines>();
        services.AddScoped<DependencyUpdateCiLaunch>();
        services.AddScoped<IDependencyUpdateLockRefresh, DependencyUpdateLockRefresh>();
        // R-24: the digest of a pinned base image is read from its registry (HTTP API, SSRF-guarded client), never derived.
        services.AddScoped<IImageDigestResolver, RegistryImageDigestResolver>();
        // R-31: the license of a NuGet target version is read from the NuGet registry (SSRF-guarded client), never from an SBOM.
        services.AddScoped<IRegistryLicenseReader, NuGetRegistryLicenseReader>();
        // R-8: npm and Packagist read the license of the target version from their registries the same way.
        services.AddScoped<IRegistryLicenseReader, NpmRegistryLicenseReader>();
        services.AddScoped<IRegistryLicenseReader, PackagistRegistryLicenseReader>();
        services.AddScoped<DependencyUpdateService>();
        services.AddScoped<IDependencyUpdateOverviewService, DependencyUpdateOverviewService>();
        services.AddScoped<IDomainEventHandler<VulnerabilityMatchesChangedEvent>, DependencyUpdatePass>();
        // The QA of an update starts when the CI run of that update succeeds (it restores that run's artifacts).
        services.AddScoped<IDomainEventHandler<PipelineRunCompletedEvent>, DependencyUpdateQaFollowUp>();
        // R-30: a green CI and QA opens the pull request (internal repository) and notifies; a closed vulnerability retires the branch.
        services.AddScoped<IDependencyUpdateMergeRequests, DependencyUpdateMergeRequests>();
        services.AddScoped<DependencyUpdateNotices>();
        services.AddScoped<DependencyUpdateReadyFollowUp>();
        services.AddScoped<IDomainEventHandler<PipelineRunCompletedEvent>>(provider => provider.GetRequiredService<DependencyUpdateReadyFollowUp>());
        services.AddScoped<DependencyUpdateBranchRetirement>();
        // R-6: the lock files a cve-lock-refresh run hands back are committed on the update's branch, then the CI starts.
        // R-29: the same follow-up also sweeps, at each pass, the rows whose run ended without this event reaching them.
        services.AddScoped<DependencyUpdateLockFollowUp>();
        services.AddScoped<IDomainEventHandler<PipelineRunCompletedEvent>>(provider => provider.GetRequiredService<DependencyUpdateLockFollowUp>());
        return services;
    }
}
