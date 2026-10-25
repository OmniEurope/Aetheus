// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AgentInstaller;
using Aetheus.Back.Components.AgentPools;
using Aetheus.Back.Components.AgentUpdate;
using Aetheus.Back.Components.AiTasks;
using Aetheus.Back.Components.Alerts;
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Components.Apache;
using Aetheus.Back.Components.AppBackups;
using Aetheus.Back.Components.AppMonitoring;
using Aetheus.Back.Components.Artifacts;
using Aetheus.Back.Components.Auth;
using Aetheus.Back.Components.Certbot;
using Aetheus.Back.Components.Cron;
using Aetheus.Back.Components.DailyRecap;
using Aetheus.Back.Components.Dashboards;
using Aetheus.Back.Components.Docker;
using Aetheus.Back.Components.Environments;
using Aetheus.Back.Components.ExternalRepos;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.GitGraph;
using Aetheus.Back.Components.Logs;
using Aetheus.Back.Components.Mail;
using Aetheus.Back.Components.ModuleLinks;
using Aetheus.Back.Components.Monitoring;
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Components.Organizations;
using Aetheus.Back.Components.PackageFeeds;
using Aetheus.Back.Components.PackageRegistry;
using Aetheus.Back.Components.PersonalAccessTokens;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Plugins;
using Aetheus.Back.Components.PortAllocation;
using Aetheus.Back.Components.PortRegistry;
using Aetheus.Back.Components.Portsentry;
using Aetheus.Back.Components.Projects;
using Aetheus.Back.Components.Releases;
using Aetheus.Back.Components.Rkhunter;
using Aetheus.Back.Components.ServerApps;
using Aetheus.Back.Components.ServerConfigurations;
using Aetheus.Back.Components.ServerModules;
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Components.ServiceConnections;
using Aetheus.Back.Components.Settings;
using Aetheus.Back.Components.SystemLogs;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Components.Teamspeak;
using Aetheus.Back.Components.TestManagement;
using Aetheus.Back.Components.Users;
using Aetheus.Back.Components.VariableLibraries;
using Aetheus.Back.Components.Vaults;
using Aetheus.Back.Components.Webhooks;
using Aetheus.Back.Components.WorkItems;
using Aetheus.Back.Seeding;

namespace Aetheus.Back.Extensions;

/// <summary>
/// Aggregates the per-module DI registrations so <c>Program.cs</c> stays focused on host wiring.
/// Each <c>Add{Module}Module()</c> method lives in its own component folder; this extension only
/// composes them.
/// </summary>
internal static class AetheusModulesExtensions
{
    public static IServiceCollection AddAetheusModules(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddSharedModule()
            .AddAuthModule()
            .AddPersonalAccessTokensModule()
            .AddAppBackupsModule()
            .AddAgentInstallerModule()
            .AddAgentUpdateModule()
            .AddServersModule()
            .AddDockerModule()
            .AddApacheModule()
            .AddTasksModule()
            .AddAiTasksModule(configuration)
            .AddPipelinesModule()
            .AddArtifactsModule()
            .AddProjectsModule()
            .AddOrganizationsModule()
            .AddLogsModule()
            .AddSystemLogsModule()
            .AddMonitoringModule()
            .AddAppMonitoringModule()
            .AddSettingsModule()
            .AddServerConfigurationsModule()
            .AddVariableLibrariesModule()
            .AddVaultsModule()
            .AddReleasesModule()
            .AddGitGraphModule()
            .AddUsersModule()
            .AddAuditModule()
            .AddEnvironmentsModule()
            .AddAgentPoolsModule()
            .AddNotificationsModule()
            .AddServiceConnectionsModule()
            .AddWebhooksModule()
            .AddAlertsModule()
            .AddAnalysisModule(configuration)
            .AddGitModule(configuration)
            .AddExternalReposModule(configuration)
            .AddWorkItemsModule()
            .AddPackageFeedsModule()
            .AddPackageRegistryModule()
            .AddTestManagementModule()
            .AddDashboardsModule()
            .AddPluginsModule()
            .AddServerModulesModule()
            .AddServerAppsModule()
            .AddCertbotModule()
            .AddCronModule()
            .AddMailModule()
            .AddTeamspeakModule()
            .AddPortsentryModule()
            .AddRkhunterModule()
            .AddModuleLinksModule()
            .AddPortRegistryModule()
            .AddPortAllocationModule()
            .AddDailyRecapModule()
            .AddSeedingModule();

        return services;
    }
}
