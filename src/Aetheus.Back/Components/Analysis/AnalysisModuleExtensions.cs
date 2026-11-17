// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Analysis;

public static class AnalysisModuleExtensions
{
    public static IServiceCollection AddAnalysisModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<DependencyTrackOptions>()
            .Bind(configuration.GetSection(DependencyTrackOptions.SectionName))
            .Validate(options => !options.Enabled || ValidEndpoint(options),
                "Dependency-Track requires an absolute HTTPS endpoint (HTTP is accepted only for loopback) and an API key.")
            .Validate(options => options.IsValid(),
                "Dependency-Track runtime and outbox settings are outside their supported range.")
            .ValidateOnStart();
        services.AddOptions<AnalysisPlatformOptions>()
            .Bind(configuration.GetSection(AnalysisPlatformOptions.SectionName))
            .Validate(options => options.IsValid(),
                "Analysis runtime quotas are outside their supported range.")
            .ValidateOnStart();
        services.AddScoped<IAnalysisRepository, AnalysisRepository>();
        // The detail rows of analysis reports past their retention, purged in batches.
        services.AddScoped<IAnalysisDetailRetentionRepository, AnalysisDetailRetentionRepository>();
        services.AddScoped<AnalysisDetailRetentionService>();
        services.AddScoped<IAnalysisService, AnalysisService>();
        // The run result reader, its stamp repository and its bounded result cache.
        services.AddScoped<IAnalysisRunResultRepository, AnalysisRunResultRepository>();
        services.AddScoped<IAnalysisRunResultService, AnalysisRunResultService>();
        services.AddSingleton<AnalysisRunResultCache>();
        services.AddScoped<IAnalysisFindingDecisionService, AnalysisFindingDecisionService>();
        services.AddScoped<Aetheus.Back.Components.Analysis.IProjectAnalysisGradeReader, ProjectAnalysisGradeRepository>();
        services.AddScoped<AnalysisPolicyEngine>();
        // The vulnerability watch (lower layer) announces each matching pass; the findings of its matches are
        // published here, through reports that belong to no run.
        services.AddScoped<CveWatchReportRepository>();
        services.AddScoped<CveWatchReportPublisher>();
        services.AddScoped<CveWatchPublication>();
        services.AddScoped<Services.DomainEvents.IDomainEventHandler<Aetheus.Back.Components.VulnerabilityWatch.VulnerabilityMatchesChangedEvent>>(
            provider => provider.GetRequiredService<CveWatchPublication>());
        services.AddScoped<CveWatchQueryRepository>();
        services.AddScoped<CveWatchRecommendationRepository>();
        services.AddScoped<CveWatchRegistryVersions>();
        services.AddScoped<CveWatchEndOfLife>();
        services.AddScoped<CveWatchRecommendationService>();
        services.AddScoped<ICveWatchQueryService, CveWatchQueryService>();
        services.AddScoped<CveWatchOverviewRepository>();
        services.AddScoped<CveWatchRunRepository>();
        services.AddScoped<CveWatchProjectRepository>();
        services.AddScoped<ICveWatchOverviewService, CveWatchOverviewService>();
        // The fix of a match: branch, lock file through a run, pull request, follow-up of what becomes of them.
        services.AddScoped<CveRemediationRepository>();
        services.AddScoped<CveRemediationGit>();
        services.AddScoped<CveRemediationLockRun>();
        services.AddScoped<CveRemediationLedger>();
        services.AddScoped<CveRemediationFollowUp>();
        services.AddScoped<ICveRemediationService, CveRemediationService>();
        services.AddScoped<Services.DomainEvents.IDomainEventHandler<Aetheus.Back.Components.Pipelines.Events.PipelineRunCompletedEvent>>(
            provider => provider.GetRequiredService<CveRemediationFollowUp>());
        services.AddScoped<Services.DomainEvents.IDomainEventHandler<Aetheus.Back.Components.Git.Events.GitBranchMovedEvent>>(
            provider => provider.GetRequiredService<CveRemediationFollowUp>());
        // Registered after the publication, which the dispatcher runs first: only a match that has its finding is announced.
        services.AddScoped<CveWatchAnnouncementRepository>();
        services.AddScoped<CveWatchHostAnnouncementRepository>();
        services.AddScoped<CveWatchAnnouncement>();
        // The nightly mail of what is still open: one hour for the platform's daily mails, the daily recap's.
        services.AddScoped<CveWatchDigest>();
        services.AddScoped<CveWatchDigestSchedule>();
        services.AddHostedService<CveWatchDigestSchedulerService>();
        services.AddScoped<Services.DomainEvents.IDomainEventHandler<Aetheus.Back.Components.VulnerabilityWatch.VulnerabilityMatchesChangedEvent>>(
            provider => provider.GetRequiredService<CveWatchAnnouncement>());
        services.AddScoped<DependencyTrackOutboxRepository>();
        services.AddScoped<IDependencyTrackOutbox, DependencyTrackOutbox>();
        services.AddScoped<IDependencyTrackSubmissionProcessor, AnalysisContinuousTracker>();
        services.AddSingleton<AnalysisIngestGate>();
        services.AddSingleton<IAnalysisPublicationSignal, PipelineHubAnalysisPublicationSignal>();
        services.AddSingleton<AnalysisReportAdmissionGate>();
        services.AddScoped<AnalysisReportAdmissionFilter>();
        services.AddHttpClient<IDependencyTrackClient, DependencyTrackClient>((provider, client) =>
        {
            var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<DependencyTrackOptions>>().Value;
            if (Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUri)) client.BaseAddress = baseUri;
            if (!string.IsNullOrWhiteSpace(options.ApiKey)) client.DefaultRequestHeaders.Add("X-Api-Key", options.ApiKey);
            client.Timeout = TimeSpan.FromMinutes(5);
        });
        services.AddHostedService<AnalysisExpirationNotificationService>();
        services.AddScoped<AnalysisDecisionExpiry>();
        services.AddHostedService<AnalysisDecisionExpiryService>();
        services.AddHostedService<DependencyTrackOutboxWorker>();
        services.AddHostedService<AnalysisContinuousSyncService>();
        services.AddHostedService<AnalysisOperationalMonitorService>();
        return services;
    }

    private static bool ValidEndpoint(DependencyTrackOptions options) =>
        !string.IsNullOrWhiteSpace(options.ApiKey)
        && Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));
}
