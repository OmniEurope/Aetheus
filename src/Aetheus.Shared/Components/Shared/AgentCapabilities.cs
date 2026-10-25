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

    /// <summary>R3-58: the agent understands the enable-capabilities field of an agent update and hands it
    /// to the Linux supervisor. A software capability gating the agent version: an older agent would run
    /// the update and silently ignore the request, so the backend refuses it by name instead.</summary>
    public const string EnableCapabilitiesOnUpdate = "agent.enable-capabilities";
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

    /// <summary>Identifies agents supporting unprivileged TCP listener observation. The backend
    /// refuses on-demand scans when this software capability is absent.</summary>
    public const string PortObservation = "ports.observe";

    /// <summary> The agent can test an outbound SMTP connection (TCP port 25) itself. Like <see
    /// cref="PortObservation"/> it is a software capability, not a grant: it gates the agent
    /// version, so the backend refuses the check by name on an older agent instead of queueing a
    /// task nobody runs. </summary>
    public const string MailOutboundProbe = "mail.outbound-probe";

    /// <summary>Recette R3-23: the agent can check what a mail stack left on the host (packages,
    /// directories, marker, ports) without privilege. A software capability gating the agent version.</summary>
    public const string MailLeftoversCheck = "mail.leftovers-check";

    /// <summary>Recette R3-59: the agent can list every installed package without privilege (read-only,
    /// no sudo). A software capability gating the agent version, unlike <see cref="PackageManagement"/>,
    /// which is a grant.</summary>
    public const string PackageInventory = "package.inventory";

    /// <summary>The agent runs build-level functions of <c>PipelineFunctionCatalog</c>
    /// (<c>type: aetheus</c> steps). A software capability gating the agent version: an agent that
    /// does not publish it never receives such a task.</summary>
    public const string PipelineFunctions = "pipeline.functions";

    /// <summary>The agent runs deployment-level functions of <c>PipelineFunctionCatalog</c>. Like
    /// <see cref="Deployment"/> it acts on a live environment, so it is published only where the
    /// deployment grant is.</summary>
    public const string DeploymentFunctions = "deployment.functions";

    public static IReadOnlyList<string> SoftwareCapabilities { get; } =
    [
        ShellExecution,
        PipelineBuild,
        ArtifactCollect,
        ArtifactRestore,
        SelfUpdate,
        EnableCapabilitiesOnUpdate,
        PortObservation,
        MailOutboundProbe,
        MailLeftoversCheck,
        PackageInventory,
        Analysis,
        AiExecution
    ];

    /// <summary>Whether an agent's declared capability list (the JSON array stored from its heartbeat)
    /// holds <paramref name="capability"/>. The single test behind both the task claim and the
    /// diagnosis of a task that can never be claimed, so the two cannot disagree.</summary>
    public static bool IsDeclared(string? capabilitiesJson, string capability) =>
        capabilitiesJson is not null
        && capabilitiesJson.Contains($"\"{capability}\"", StringComparison.Ordinal);

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
        OperationKind.PipelineFunction => PipelineFunctions,
        OperationKind.PipelineDeploymentFunction => DeploymentFunctions,
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
        >= OperationKind.ApacheReload and <= OperationKind.ApacheDeleteSite => ApacheManagement,
        >= OperationKind.CertbotObtain and <= OperationKind.CertbotRemoveDomain => CertbotManagement,
        >= OperationKind.RkhunterScan and <= OperationKind.RkhunterPropupd => Analysis,
        >= OperationKind.FirewallAllow and <= OperationKind.FirewallSetEnabled => FirewallManagement,
        _ => null
    };

    private static string? RequiredServiceCapability(OperationKind operation) => operation switch
    {
        >= OperationKind.ServiceStart and <= OperationKind.ServiceGetLogs => ServiceManagement,
        OperationKind.ServiceInstall or OperationKind.ServiceUninstall => PackageManagement,
        OperationKind.PackageInventory => PackageInventory,
        >= OperationKind.MailStartPostfix and <= OperationKind.MailPurge => MailManagement,
        OperationKind.MailMasterAccess or OperationKind.MailSetQuota or OperationKind.MailRetryTuning => MailManagement,
        OperationKind.MailOutboundProbe => MailOutboundProbe,
        OperationKind.MailLeftoversCheck => MailLeftoversCheck,
        >= OperationKind.PortsentryStart and <= OperationKind.PortsentrySetup => PortsentryManagement,
        >= OperationKind.CronSave and <= OperationKind.CronDelete => CronManagement,
        >= OperationKind.TeamspeakServerQuery and <= OperationKind.TeamspeakGetLogs => TeamspeakManagement,
        _ => null
    };

    /// <summary>
    /// The capability the confirming heartbeat must report once an
    /// <see cref="Agents.AgentEnableableCapabilities"/> token is installed (R1-5, ADR-055).
    /// <see cref="Agents.AgentEnableableCapabilities.RkhunterManage"/> reports none, so its grant is not
    /// confirmed by the heartbeat.
    /// </summary>
    public static string? ForEnableableToken(string token) => token switch
    {
        Agents.AgentEnableableCapabilities.PatchManage => PatchManagement,
        Agents.AgentEnableableCapabilities.PackageManage => PackageManagement,
        Agents.AgentEnableableCapabilities.ServiceEnable => ServiceManagement,
        Agents.AgentEnableableCapabilities.CronManage => CronManagement,
        Agents.AgentEnableableCapabilities.ApacheManage => ApacheManagement,
        Agents.AgentEnableableCapabilities.CertbotManage => CertbotManagement,
        Agents.AgentEnableableCapabilities.PortsentryManage => PortsentryManagement,
        Agents.AgentEnableableCapabilities.FirewallManage => FirewallManagement,
        Agents.AgentEnableableCapabilities.MailSetup => MailManagement,
        Agents.AgentEnableableCapabilities.TeamspeakSetup => TeamspeakManagement,
        _ => null
    };
}
