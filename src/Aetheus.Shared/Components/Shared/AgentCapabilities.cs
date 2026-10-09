// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Shared.Components.Shared;

/// <summary>Stable identifiers published by agents and consumed by assignment guards.</summary>
public static class AgentCapabilities
{
    public const string ShellExecution = "shell.execute";
    public const string DockerExecution = "docker.execute";
    public const string PipelineBuild = "pipeline.build";
    public const string ArtifactCollect = "artifact.collect";
    public const string ArtifactRestore = "artifact.restore";
    public const string Deployment = "deployment.apply";
    public const string SelfUpdate = "agent.self-update";
    public const string ServiceManagement = "service.manage";
    public const string PackageManagement = "package.manage";
    public const string PatchManagement = "patch.manage";
    public const string FirewallManagement = "firewall.manage";
    public const string ApacheManagement = "apache.manage";
    public const string CertbotManagement = "certbot.manage";
    public const string MailManagement = "mail.manage";
    public const string TeamspeakManagement = "teamspeak.manage";
    public const string PortsentryManagement = "portsentry.manage";
    public const string CronManagement = "cron.manage";
    public const string Analysis = "analysis.run";
    public const string AiExecution = "ai.run";
    public const string Backup = "backup.manage";

    /// <summary>
    /// PLAN-005 lot 2: the agent can report which TCP ports are actually listening on the host.
    /// It is a software capability, not a grant: <c>ss -ltn</c> / <c>Get-NetTCPConnection</c> need no
    /// privilege. What it really gates is the agent VERSION - an agent predating the collector never
    /// publishes it, and the backend then refuses the on-demand scan by name instead of queueing a
    /// task nobody can execute.
    /// </summary>
    public const string PortObservation = "ports.observe";

    /// <summary>
    /// Recette R2-080: the agent can test an outbound SMTP connection (TCP port 25) itself. Like
    /// <see cref="PortObservation"/> it is a software capability, not a grant: it gates the agent version,
    /// so the backend refuses the check by name on an older agent instead of queueing a task nobody runs.
    /// </summary>
    public const string MailOutboundProbe = "mail.outbound-probe";

    public static IReadOnlyList<string> SoftwareCapabilities { get; } =
    [
        ShellExecution,
        PipelineBuild,
        ArtifactCollect,
        ArtifactRestore,
        SelfUpdate,
        PortObservation,
        MailOutboundProbe,
        Analysis,
        AiExecution
    ];

    public static string? RequiredFor(OperationKind operation) =>
        RequiredPipelineCapability(operation)
        ?? RequiredInfrastructureCapability(operation)
        ?? RequiredServiceCapability(operation);

    private static string? RequiredPipelineCapability(OperationKind operation) => operation switch
    {
        OperationKind.None => ShellExecution,
        OperationKind.AiRun => AiExecution,
        OperationKind.AgentSelfUpdate => SelfUpdate,
        OperationKind.PortsObserve => PortObservation,
        OperationKind.PipelineCollectArtifacts => ArtifactCollect,
        OperationKind.PipelineRestoreArtifacts => ArtifactRestore,
        OperationKind.PipelineDeploy => Deployment,
        // Evidence-producing steps that touch nothing deployed, so the build capability and not the
        // deployment grant: smoke only probes an already-deployed origin and reports findings; a
        // dotnet-test step runs a build output and publishes a classified status; a gate-status step
        // only reads statuses earlier steps published; a mutation step, like coverage, reads a report
        // the build produced and publishes a metric.
        OperationKind.PipelineSmoke or OperationKind.PipelineDotnetTest or OperationKind.PipelineGateStatus
            or OperationKind.PipelinePublishMutation => PipelineBuild,
        // The blue-green operations mutate a live environment, so they sit behind the deployment
        // grant alongside PipelineDeploy rather than the evidence capability.
        >= OperationKind.BlueGreenMigrate and <= OperationKind.BlueGreenRevert => Deployment,
        >= OperationKind.PipelineCreateRelease and <= OperationKind.PipelinePublishObservabilityBundle => PipelineBuild,
        >= OperationKind.BackupExecute and <= OperationKind.BackupRestore => Backup,
        _ => null
    };

    private static string? RequiredInfrastructureCapability(OperationKind operation) => operation switch
    {
        >= OperationKind.DockerRestartContainer and <= OperationKind.DockerBuildImage => DockerExecution,
        OperationKind.SystemPackageUpgrade => PatchManagement,
        >= OperationKind.ApacheReload and <= OperationKind.ApacheApplyConfigSet => ApacheManagement,
        >= OperationKind.CertbotObtain and <= OperationKind.CertbotRenewalCheck => CertbotManagement,
        >= OperationKind.RkhunterScan and <= OperationKind.RkhunterPropupd => Analysis,
        >= OperationKind.FirewallAllow and <= OperationKind.FirewallSetEnabled => FirewallManagement,
        _ => null
    };

    private static string? RequiredServiceCapability(OperationKind operation) => operation switch
    {
        >= OperationKind.ServiceStart and <= OperationKind.ServiceGetLogs => ServiceManagement,
        OperationKind.ServiceInstall or OperationKind.ServiceUninstall => PackageManagement,
        >= OperationKind.MailStartPostfix and <= OperationKind.MailPurge => MailManagement,
        OperationKind.MailOutboundProbe => MailOutboundProbe,
        >= OperationKind.PortsentryStart and <= OperationKind.PortsentrySetup => PortsentryManagement,
        >= OperationKind.CronSave and <= OperationKind.CronDelete => CronManagement,
        >= OperationKind.TeamspeakServerQuery and <= OperationKind.TeamspeakGetLogs => TeamspeakManagement,
        _ => null
    };
}
