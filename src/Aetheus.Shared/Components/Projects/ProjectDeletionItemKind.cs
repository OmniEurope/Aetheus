// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Projects;

/// <summary>
/// Recette R2-122: one kind of data that deleting a project removes with it. The deletion preview
/// counts each kind, and the audit entry of the deletion lists the same counts.
/// </summary>
public enum ProjectDeletionItemKind
{
    Pipelines = 0,
    PipelineRuns = 1,
    PipelineStepRuns = 2,
    Tasks = 3,
    TaskLogLines = 4,
    Approvals = 5,
    Artifacts = 6,
    Environments = 7,
    MonitoredApps = 8,
    TelemetryRecords = 9,
    BackupPolicies = 10,
    ProjectServers = 11,
    Vaults = 12,
    VaultSecrets = 13,
    VariableLibraries = 14,
    ServiceConnections = 15,
    PackageFeeds = 16,
    PortReservations = 17,
    Subscriptions = 18,
    Releases = 19,
    GitRepositories = 20,
    GitConnections = 21,
    AnalysisReports = 22,
    AnalysisFindings = 23,
    WorkItems = 24,
    TestSuites = 25,
    AiTasks = 26
}
