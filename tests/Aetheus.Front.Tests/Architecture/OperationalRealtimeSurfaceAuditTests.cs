// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Tests.Architecture;

/// <summary>
/// Guards the pages whose displayed state can change without a local user action. Static editors
/// and historical detail views are intentionally excluded; operational lists and dashboards must
/// retain their SignalR/event subscription and reconnect path, or their explicit bounded refresh
/// loop when the underlying source is stream-only.
/// </summary>
public sealed class OperationalRealtimeSurfaceAuditTests
{
    private static readonly IReadOnlyDictionary<string, string[]> RequiredContracts =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Components/Dashboards/Home.razor.cs"] =
                ["ServerHeartbeat", "PipelineRunStarted", "EntityChanged", "RejoinOnReconnect"],
            // An agent update refreshes the update count and the task chip.
            ["Components/Servers/Servers.razor.cs"] =
                ["ServerHeartbeat", "ServerOffline", "AgentUpdateConfirmed", "AgentUpdateFailed", "RejoinOnReconnect"],
            ["Components/Shared/TaskTrackerService.cs"] =
                ["TaskCompleted", "AgentUpdateConfirmed", "AgentUpdateFailed", "RejoinOnReconnect"],
            ["Components/Shared/ServerDetailLoader.cs"] =
                ["Heartbeat", "TaskCompleted", "RejoinOnReconnect"],
            ["Components/Pipelines/PipelinesList.razor.cs"] =
                ["PipelineRunStarted", "PipelineRunCompleted", "EntityChanged", "RejoinOnReconnect"],
            ["Components/Pipelines/PipelineRunLiveConnection.cs"] =
                ["PipelineRunCompleted", "StepCompleted", "RejoinOnReconnect"],
            ["Components/Tasks/TaskListView.razor.cs"] =
                ["TaskQueued", "TaskCompleted", "RejoinOnReconnect"],
            ["Components/Alerts/Alerts.razor.cs"] =
                ["AlertRuleChanged", "RejoinOnReconnect"],
            ["Components/Releases/ReleasesList.razor.cs"] =
                ["ReleaseCreated", "ReleaseStatusChanged", "RejoinOnReconnect"],
            ["Components/Shared/PendingApprovalsService.cs"] =
                ["ApprovalRequired", "ApprovalResolved", "PipelineRunCompleted", "RejoinOnReconnect"],
            ["Components/Git/GitRepositoryRealtimeSubscription.cs"] =
                ["BranchesChanged", "CommitsChanged", "RejoinOnReconnect"],
            // R1-69: the Supervision follows project changes and covers the rest with a bounded periodic reload.
            ["Components/Monitoring/Supervision.razor.cs"] =
                ["FollowEntitiesAsync", "ResourceType.Project", "TrailingReloadCoalescer", "PeriodicTimer"],
            ["Components/Projects/MonitoredAppsList.razor.cs"] =
                ["EntityChanged", "ResourceType.Project", "RejoinOnReconnect"],
            ["Components/Analysis/AnalysisPortfolio.razor.cs"] =
                ["EntityChanged", "ResourceType.Project", "RejoinOnReconnect"],
            ["Components/ServiceConnections/ServiceConnections.razor.cs"] =
                ["EntityChanged", "ResourceType.ServiceConnection", "RejoinOnReconnect"],
            ["Components/Pipelines/PipelineTemplates.razor.cs"] =
                ["EntityChanged", "ResourceType.PipelineTemplate", "RejoinOnReconnect"],
            ["Components/AiTasks/AiTasks.razor.cs"] =
                ["TaskTracker.OnTaskCompleted", "OperationKind.AiRun", "FindRunResultAsync"],
            ["Components/AppBackups/Backups.razor.cs"] =
                ["OperationalRealtimeEvents.BackupChanged", "RejoinOnReconnect", "TrailingReloadCoalescer"],
            ["Components/Shared/RealtimeAdminGridPageBase.cs"] =
                ["AdminEntitySubscription", "TrailingReloadCoalescer"],
            ["Components/PackageFeeds/PackageFeedsAdmin.razor.cs"] =
                ["RealtimeAdminGridPageBase", "AdminEntities.PackageFeed"],
            ["Components/PackageFeeds/PackageFeedPackagesDialog.razor.cs"] =
                ["AdminEntitySubscription", "AdminEntities.PackageFeed", "TrailingReloadCoalescer"],
            ["Components/PackageRegistry/PackageRegistryAdmin.razor.cs"] =
                ["RealtimeAdminGridPageBase", "AdminEntities.PackageRegistry"],
            ["Components/PackageRegistry/PackageRegistryPackageDialog.razor.cs"] =
                ["AdminEntitySubscription", "AdminEntities.PackageRegistry", "TrailingReloadCoalescer"],
            ["Components/Projects/Projects.razor.cs"] =
                ["EntityChanged", "PipelineRunStarted", "ResourceType.Project", "RejoinOnReconnect"],
            ["Components/Projects/ProjectDetailSections/ProjectOverviewSection.razor.cs"] =
                ["PipelineRunStarted", "PipelineRunCompleted", "RejoinOnReconnect"],
            ["Components/Releases/ReleaseDetail.razor.cs"] =
                ["ReleaseStatusChanged", "RejoinOnReconnect"],
            ["Components/Logs/Logs.razor.cs"] =
                ["LogReceived", "LogsReceived", "RejoinOnReconnect"],
            // Administrative updates arrive through the hub rather than polling.
            ["Components/Logs/SystemLogs.razor.cs"] =
                ["AdminEntitySubscription", "AdminEntities.SystemLog", "TrailingReloadCoalescer"],
            ["Components/Projects/ProjectDetailSections/ProjectQualitySection.razor.cs"] =
                ["EntityChanged", "ResourceType.Project", "RejoinOnReconnect"],
            ["Components/Audit/AuditLogs.razor.cs"] =
                ["AdminEntitySubscription", "AdminEntities.AuditLog", "TrailingReloadCoalescer"],
            ["Components/Users/UserEdit.razor.cs"] =
                ["AdminEntitySubscription", "AdminEntities.Role"],
            ["Components/Shared/EntityLiveListBase.cs"] =
                ["EntityChanged", "JoinEntityUpdates", "RejoinOnReconnect"],
            ["Components/Pipelines/PipelineFleet.razor.cs"] =
                ["FollowEntitiesAsync", "ResourceType.PipelineTemplate", "TrailingReloadCoalescer"],
            ["Components/Projects/ProjectDetailSections/ProjectArtifactsSection.razor.cs"] =
                ["PipelineRunCompleted"],
            ["Components/Environments/EnvironmentsList.razor.cs"] =
                ["EntityChanged", "ResourceType.Environment", "RejoinOnReconnect"],
            ["Components/Shared/VariableLibrariesList.razor.cs"] =
                ["EntityChanged", "ResourceType.VariableLibrary", "RejoinOnReconnect"],
            ["Components/Shared/VaultsList.razor.cs"] =
                ["FollowEntitiesAsync", "ResourceType.Vault"],
            // Views refreshed by server-pushed events.
            ["Components/Shared/ServerLiveFeed.cs"] =
                ["ServerHeartbeat", "TaskCompleted", "JoinServerGroup", "RejoinOnReconnect", "TrailingReloadCoalescer"],
            ["Components/Shared/EntityOperationalFeed.cs"] =
                ["JoinEntityUpdates", "RejoinOnReconnect", "TrailingReloadCoalescer"],
            ["Components/Servers/ServerDetailSections/ServerMailStatusList.razor.cs"] =
                ["ServerLiveFeed", "ServerLiveFeedTriggers.HeartbeatAndTasks"],
            ["Components/Servers/ServerDetailSections/ServerRkhunterSection.razor.cs"] =
                ["ServerLiveFeed", "ServerLiveFeedTriggers.Heartbeat"],
            ["Components/Servers/ServerDetailSections/ServerTeamspeakSection.razor.cs"] =
                ["ServerLiveFeed", "ServerLiveFeedTriggers.Heartbeat"],
            ["Components/Servers/ServerDetailSections/ServerServicesSection.razor.cs"] =
                ["ServiceLogAutoRefresh", "LogReceived", "RejoinOnReconnect"],
            ["Components/Shared/AppErrorsView.razor.cs"] =
                ["EntityOperationalFeed", "OperationalRealtimeEvents.AppTelemetryChanged"],
            ["Components/Logs/AppLogsView.razor.cs"] =
                ["EntityOperationalFeed", "OperationalRealtimeEvents.AppTelemetryChanged"],
            ["Components/Logs/Performance.razor.cs"] =
                ["AdminEntitySubscription", "AdminEntities.ApiPerformance", "TrailingReloadCoalescer"]
        };

    [Fact]
    public void OperationalSurfaces_KeepTheirRealtimeContracts()
    {
        var front = Path.Combine(FindRepoRoot(), "src", "Aetheus.Front");
        var missing = new List<string>();

        foreach (var (relativePath, contracts) in RequiredContracts)
        {
            var path = Path.Combine(front, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                missing.Add($"{relativePath}: file missing");
                continue;
            }

            var source = File.ReadAllText(path);
            missing.AddRange(contracts
                .Where(contract => !source.Contains(contract, StringComparison.Ordinal))
                .Select(contract => $"{relativePath}: missing {contract}"));
        }

        Assert.True(RequiredContracts.Count >= 27, "Realtime inventory unexpectedly shrank.");
        Assert.Empty(missing);
    }

    private static string FindRepoRoot() => Aetheus.Front.Tests.Architecture.RepositoryScan.Root;
}
