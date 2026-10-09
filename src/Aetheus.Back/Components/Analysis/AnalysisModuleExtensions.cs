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
        // PLAN-013 lot 2: the detail rows of analysis reports past their retention, purged in batches.
        services.AddScoped<IAnalysisDetailRetentionRepository, AnalysisDetailRetentionRepository>();
        services.AddScoped<AnalysisDetailRetentionService>();
        services.AddScoped<IAnalysisService, AnalysisService>();
        // Recette R-485: the run result reader, its stamp repository and its bounded result cache.
        services.AddScoped<IAnalysisRunResultRepository, AnalysisRunResultRepository>();
        services.AddScoped<IAnalysisRunResultService, AnalysisRunResultService>();
        services.AddSingleton<AnalysisRunResultCache>();
        services.AddScoped<IAnalysisFindingDecisionService, AnalysisFindingDecisionService>();
        services.AddScoped<Aetheus.Back.Components.Analysis.IProjectAnalysisGradeReader, ProjectAnalysisGradeRepository>();
        services.AddScoped<AnalysisPolicyEngine>();
        services.AddScoped<DependencyTrackOutboxRepository>();
        services.AddScoped<IDependencyTrackOutbox, DependencyTrackOutbox>();
        services.AddScoped<IDependencyTrackSubmissionProcessor, AnalysisContinuousTracker>();
        services.AddSingleton<AnalysisIngestGate>();
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
