// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using Environment = Aetheus.Back.Data.Entities.Environment;

namespace Aetheus.Back.Data;

public class AppDbContext(
    DbContextOptions<AppDbContext> options,
    TimeProvider? timeProvider = null) : DbContext(options)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    public DbSet<Server> Servers => Set<Server>();
    public DbSet<RegistrationToken> RegistrationTokens => Set<RegistrationToken>();
    public DbSet<ServerToken> ServerTokens => Set<ServerToken>();
    public DbSet<ServerTask> Tasks => Set<ServerTask>();
    public DbSet<AgentUpdateRequest> AgentUpdateRequests => Set<AgentUpdateRequest>();
    public DbSet<TaskLog> TaskLogs => Set<TaskLog>();
    public DbSet<Pipeline> Pipelines => Set<Pipeline>();
    public DbSet<PipelineFavorite> PipelineFavorites => Set<PipelineFavorite>();
    public DbSet<PipelineRun> PipelineRuns => Set<PipelineRun>();
    public DbSet<PipelineStepRun> PipelineStepRuns => Set<PipelineStepRun>();
    public DbSet<PipelineTemplate> PipelineTemplates => Set<PipelineTemplate>();
    public DbSet<PipelineTemplateVersion> PipelineTemplateVersions => Set<PipelineTemplateVersion>();
    public DbSet<ServiceInfo> ServiceInfos => Set<ServiceInfo>();
    public DbSet<ServerMetric> ServerMetrics => Set<ServerMetric>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<Secret> Secrets => Set<Secret>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<OrganizationMember> OrganizationMembers => Set<OrganizationMember>();
    public DbSet<DockerContainer> DockerContainers => Set<DockerContainer>();
    public DbSet<DockerImage> DockerImages => Set<DockerImage>();
    public DbSet<DockerComposeStack> DockerComposeStacks => Set<DockerComposeStack>();
    public DbSet<DockerNetwork> DockerNetworks => Set<DockerNetwork>();
    public DbSet<DockerVolume> DockerVolumes => Set<DockerVolume>();
    public DbSet<ApacheState> ApacheStates => Set<ApacheState>();
    public DbSet<ApacheModule> ApacheModules => Set<ApacheModule>();
    public DbSet<ApacheVirtualHost> ApacheVirtualHosts => Set<ApacheVirtualHost>();
    public DbSet<CertbotCertificate> CertbotCertificates => Set<CertbotCertificate>();
    public DbSet<CertbotState> CertbotStates => Set<CertbotState>();
    public DbSet<MailState> MailStates => Set<MailState>();
    public DbSet<MailDomain> MailDomains => Set<MailDomain>();
    public DbSet<MailAccount> MailAccounts => Set<MailAccount>();
    public DbSet<MailAlias> MailAliases => Set<MailAlias>();
    public DbSet<TeamspeakState> TeamspeakStates => Set<TeamspeakState>();
    public DbSet<TeamspeakChannel> TeamspeakChannels => Set<TeamspeakChannel>();
    public DbSet<TeamspeakClient> TeamspeakClients => Set<TeamspeakClient>();
    public DbSet<TeamspeakBan> TeamspeakBans => Set<TeamspeakBan>();
    public DbSet<PortsentryState> PortsentryStates => Set<PortsentryState>();
    public DbSet<PortsentryBlockedIp> PortsentryBlockedIps => Set<PortsentryBlockedIp>();
    public DbSet<PortsentryWhitelistIp> PortsentryWhitelistIps => Set<PortsentryWhitelistIp>();
    public DbSet<RkhunterState> RkhunterStates => Set<RkhunterState>();
    public DbSet<SecurityUpdatesState> SecurityUpdatesStates => Set<SecurityUpdatesState>();
    public DbSet<FirewallState> FirewallStates => Set<FirewallState>();
    public DbSet<BackupPolicy> BackupPolicies => Set<BackupPolicy>();
    public DbSet<BackupRun> BackupRuns => Set<BackupRun>();
    public DbSet<RkhunterWarning> RkhunterWarnings => Set<RkhunterWarning>();
    public DbSet<RkhunterScanResult> RkhunterScanResults => Set<RkhunterScanResult>();
    public DbSet<ModuleLink> ModuleLinks => Set<ModuleLink>();
    public DbSet<ServerPortReservation> ServerPortReservations => Set<ServerPortReservation>();
    public DbSet<ServerPortRange> ServerPortRanges => Set<ServerPortRange>();
    public DbSet<VariableLibrary> VariableLibraries => Set<VariableLibrary>();
    public DbSet<VariableLibraryEntry> VariableLibraryEntries => Set<VariableLibraryEntry>();
    public DbSet<VariableLibraryEntryVersion> VariableLibraryEntryVersions => Set<VariableLibraryEntryVersion>();
    public DbSet<Vault> Vaults => Set<Vault>();
    public DbSet<VaultSecret> VaultSecrets => Set<VaultSecret>();
    public DbSet<VaultSecretVersion> VaultSecretVersions => Set<VaultSecretVersion>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<ResourcePermission> ResourcePermissions => Set<ResourcePermission>();
    public DbSet<ExternalLogin> ExternalLogins => Set<ExternalLogin>();
    public DbSet<Release> Releases => Set<Release>();
    public DbSet<ReleaseRollback> ReleaseRollbacks => Set<ReleaseRollback>();
    public DbSet<ProjectReleaseSeries> ProjectReleaseSeries => Set<ProjectReleaseSeries>();
    public DbSet<ProjectReleaseNumberAllocation> ProjectReleaseNumberAllocations => Set<ProjectReleaseNumberAllocation>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<Environment> Environments => Set<Environment>();
    public DbSet<EnvironmentServer> EnvironmentServers => Set<EnvironmentServer>();
    public DbSet<EnvironmentProjectServer> EnvironmentProjectServers => Set<EnvironmentProjectServer>();
    public DbSet<PipelineApproval> PipelineApprovals => Set<PipelineApproval>();
    public DbSet<PipelineResourceLock> PipelineResourceLocks => Set<PipelineResourceLock>();
    public DbSet<AgentPool> AgentPools => Set<AgentPool>();
    public DbSet<AgentPoolServer> AgentPoolServers => Set<AgentPoolServer>();
    public DbSet<PipelineArtifact> PipelineArtifacts => Set<PipelineArtifact>();
    public DbSet<PipelineRunArtifactInput> PipelineRunArtifactInputs => Set<PipelineRunArtifactInput>();
    public DbSet<ArtifactStorageMeasurement> ArtifactStorageMeasurements => Set<ArtifactStorageMeasurement>();
    public DbSet<NotificationChannel> NotificationChannels => Set<NotificationChannel>();
    public DbSet<NotificationRule> NotificationRules => Set<NotificationRule>();
    public DbSet<NotificationDelivery> NotificationDeliveries => Set<NotificationDelivery>();
    public DbSet<ProjectSubscription> ProjectSubscriptions => Set<ProjectSubscription>();
    public DbSet<UserNotificationPreference> UserNotificationPreferences => Set<UserNotificationPreference>();
    public DbSet<UserPreference> UserPreferences => Set<UserPreference>();
    public DbSet<MailTemplate> MailTemplates => Set<MailTemplate>();
    public DbSet<ServiceConnection> ServiceConnections => Set<ServiceConnection>();
    public DbSet<WebhookSubscription> WebhookSubscriptions => Set<WebhookSubscription>();
    public DbSet<TestResult> TestResults => Set<TestResult>();
    public DbSet<CoverageResult> CoverageResults => Set<CoverageResult>();
    public DbSet<LintResult> LintResults => Set<LintResult>();
    public DbSet<RunMetric> RunMetrics => Set<RunMetric>();
    public DbSet<PipelineMigrationGateVerdict> PipelineMigrationGateVerdicts => Set<PipelineMigrationGateVerdict>();
    public DbSet<AlertRule> AlertRules => Set<AlertRule>();
    public DbSet<EnvironmentCheck> EnvironmentChecks => Set<EnvironmentCheck>();
    public DbSet<GitConnection> GitConnections => Set<GitConnection>();
    public DbSet<PullRequest> PullRequests => Set<PullRequest>();
    public DbSet<BranchPolicy> BranchPolicies => Set<BranchPolicy>();
    public DbSet<WorkItem> WorkItems => Set<WorkItem>();
    public DbSet<PackageFeed> PackageFeeds => Set<PackageFeed>();
    public DbSet<PackageEntry> PackageEntries => Set<PackageEntry>();
    public DbSet<TestSuite> TestSuites => Set<TestSuite>();
    public DbSet<TestCase> TestCases => Set<TestCase>();
    public DbSet<Dashboard> Dashboards => Set<Dashboard>();
    public DbSet<DashboardWidget> DashboardWidgets => Set<DashboardWidget>();
    public DbSet<PluginRegistration> PluginRegistrations => Set<PluginRegistration>();
    public DbSet<ServerModule> ServerModules => Set<ServerModule>();
    public DbSet<ServerApp> ServerApps => Set<ServerApp>();
    public DbSet<GitInternalRepo> GitInternalRepos => Set<GitInternalRepo>();
    public DbSet<BranchProtectionRule> BranchProtectionRules => Set<BranchProtectionRule>();
    public DbSet<ProjectServer> ProjectServers => Set<ProjectServer>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<PersonalAccessToken> PersonalAccessTokens => Set<PersonalAccessToken>();
    public DbSet<GitBranch> GitBranches => Set<GitBranch>();
    public DbSet<GitCommit> GitCommits => Set<GitCommit>();
    public DbSet<MonitoredApp> MonitoredApps => Set<MonitoredApp>();
    public DbSet<AppHealthSample> AppHealthSamples => Set<AppHealthSample>();
    public DbSet<AppHealthHourly> AppHealthHourly => Set<AppHealthHourly>();
    public DbSet<AppMetricSample> AppMetricSamples => Set<AppMetricSample>();
    public DbSet<AppMetricHourly> AppMetricHourly => Set<AppMetricHourly>();
    public DbSet<AppMetricName> AppMetricNames => Set<AppMetricName>();
    public DbSet<AppMetricThreshold> AppMetricThresholds => Set<AppMetricThreshold>();
    public DbSet<AppLogEntry> AppLogEntries => Set<AppLogEntry>();
    public DbSet<AppErrorEvent> AppErrorEvents => Set<AppErrorEvent>();
    public DbSet<AnalysisReport> AnalysisReports => Set<AnalysisReport>();
    public DbSet<AnalysisFinding> AnalysisFindings => Set<AnalysisFinding>();
    public DbSet<AnalysisFindingOccurrence> AnalysisFindingOccurrences => Set<AnalysisFindingOccurrence>();
    public DbSet<AnalysisFindingDecision> AnalysisFindingDecisions => Set<AnalysisFindingDecision>();
    public DbSet<AnalysisMetric> AnalysisMetrics => Set<AnalysisMetric>();
    public DbSet<AnalysisComponent> AnalysisComponents => Set<AnalysisComponent>();
    public DbSet<AnalysisPolicy> AnalysisPolicies => Set<AnalysisPolicy>();
    public DbSet<AnalysisPolicyRevision> AnalysisPolicyRevisions => Set<AnalysisPolicyRevision>();
    public DbSet<AnalysisPolicyException> AnalysisPolicyExceptions => Set<AnalysisPolicyException>();
    public DbSet<AnalysisEvaluation> AnalysisEvaluations => Set<AnalysisEvaluation>();
    public DbSet<AnalysisTrackingProject> AnalysisTrackingProjects => Set<AnalysisTrackingProject>();
    public DbSet<AnalysisVulnerabilityObservation> AnalysisVulnerabilityObservations => Set<AnalysisVulnerabilityObservation>();
    public DbSet<DastExecutionLease> DastExecutionLeases => Set<DastExecutionLease>();
    public DbSet<DependencyTrackOutboxItem> DependencyTrackOutboxItems => Set<DependencyTrackOutboxItem>();
    public DbSet<AppVisitorIdentity> AppVisitorIdentities => Set<AppVisitorIdentity>();
    public DbSet<AppAnalyticsEvent> AppAnalyticsEvents => Set<AppAnalyticsEvent>();
    public DbSet<AppAnalyticsSession> AppAnalyticsSessions => Set<AppAnalyticsSession>();
    public DbSet<AppAnalyticsPeriodIdentity> AppAnalyticsPeriodIdentities => Set<AppAnalyticsPeriodIdentity>();
    public DbSet<AppAnalyticsAggregate> AppAnalyticsAggregates => Set<AppAnalyticsAggregate>();
    public DbSet<AppAnalyticsPageAggregate> AppAnalyticsPageAggregates => Set<AppAnalyticsPageAggregate>();
    public DbSet<AppAnalyticsRejection> AppAnalyticsRejections => Set<AppAnalyticsRejection>();
    public DbSet<AppAnalyticsIngestVolume> AppAnalyticsIngestVolumes => Set<AppAnalyticsIngestVolume>();
    public DbSet<RegistryPackage> RegistryPackages => Set<RegistryPackage>();
    public DbSet<RegistryPackageVersion> RegistryPackageVersions => Set<RegistryPackageVersion>();
    public DbSet<RegistryPackageFile> RegistryPackageFiles => Set<RegistryPackageFile>();
    public DbSet<AiRunnerProfile> AiRunnerProfiles => Set<AiRunnerProfile>();
    public DbSet<AiTaskDefinition> AiTaskDefinitions => Set<AiTaskDefinition>();
    public DbSet<AiTaskTrigger> AiTaskTriggers => Set<AiTaskTrigger>();
    public DbSet<AiRunResult> AiRunResults => Set<AiRunResult>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }

    /// <summary>
    /// F-009: Centralized timestamp management. Sets CreatedAt on Added entities and
    /// UpdatedAt on Modified entities using the injected TimeProvider, eliminating
    /// DateTime.UtcNow defaults scattered across ~68 entity files.
    /// </summary>
    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        PrepareTrackedEntities();
        return await base.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public override int SaveChanges()
    {
        PrepareTrackedEntities();
        return base.SaveChanges();
    }

    private void PrepareTrackedEntities()
    {
        RejectModifiedTemplateVersions();
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State == EntityState.Added)
            {
                if (entry.Metadata.FindProperty("CreatedAt") is not null)
                    entry.Property("CreatedAt").CurrentValue = now;
                if (entry.Metadata.FindProperty("Timestamp") is not null
                    && entry.Entity is not Entities.ServerMetric  // ServerMetric.Timestamp is collector-sourced
                    && entry.Entity is not Entities.AppHealthSample // AppHealthSample.Timestamp is probe-sourced (moment of check)
                    && entry.Entity is not Entities.AppMetricSample // AppMetricSample.Timestamp is emit-sourced (OTLP data point)
                    && entry.Entity is not Entities.AppLogEntry     // AppLogEntry.Timestamp is emit-sourced (OTLP log record)
                    && entry.Entity is not Entities.AuditLog)     // AuditLog.Timestamp is hash-covered, set pre-insert (F-003)
                    entry.Property("Timestamp").CurrentValue = now;
                if (entry.Metadata.FindProperty("LastUpdated") is not null)
                    entry.Property("LastUpdated").CurrentValue = now;
                if (entry.Metadata.FindProperty("UpdatedAt") is not null)
                    entry.Property("UpdatedAt").CurrentValue = now;
            }
            else if (entry.State == EntityState.Modified)
            {
                if (entry.Metadata.FindProperty("UpdatedAt") is not null)
                    entry.Property("UpdatedAt").CurrentValue = now;
                if (entry.Metadata.FindProperty("LastUpdated") is not null)
                    entry.Property("LastUpdated").CurrentValue = now;
            }
        }
    }

    private void RejectModifiedTemplateVersions()
    {
        if (ChangeTracker.Entries<PipelineTemplateVersion>()
            .Any(entry => entry.State == EntityState.Modified))
            throw new InvalidOperationException("Published pipeline template versions are immutable.");
    }

}
