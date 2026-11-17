// SPDX-License-Identifier: EUPL-1.2
using System.Collections.Frozen;

namespace Aetheus.Back.Components.Tasks;

/// <summary>
/// What a caller of <c>POST /api/tasks/operation</c> must hold on the server, beyond Server Write, to queue
/// one <see cref="OperationKind"/>.
/// </summary>
/// <param name="Level">The server level: <see cref="Permission.Write"/>, or <see cref="Permission.Admin"/>
/// for a kind whose own endpoint (or only producer) requires Server Admin.</param>
/// <param name="ActionKey">The named permission (<see cref="PermissionKeys"/>) the kind's own endpoint checks;
/// null when it checks only a level.</param>
public sealed record OperationPermission(Permission Level, string? ActionKey = null);

/// <summary>
/// BACK-003: the generic typed-operation endpoint must never open a door its dedicated endpoint keeps
/// closed. Each kind requires what the endpoint that normally queues it requires: the named permission of
/// the services, system upgrade and firewall endpoints, Server Admin for the Admin-gated endpoints (image
/// build and removal, prune, compose file deploy, container exec, setups, reboot, agent self-update,
/// configuration deploy, certificate creation) and for the kinds only pipelines, schedulers and the
/// project deletion flow queue (their own paths authorize the run's owner as Server Admin, or Project
/// Admin). When several endpoints queue one kind, the strictest wins: the generic endpoint lets the caller
/// pick the target freely (a raw TeamSpeak ServerQuery line reaches the token commands). A kind whose
/// endpoints need less than Server Write still needs Server Write here, as it did before. An unlisted kind
/// has no requirement and is refused: a new kind must be classified before this endpoint accepts it.
/// </summary>
public static class OperationKindPermissions
{
    private static readonly OperationPermission Write = new(Permission.Write);
    private static readonly OperationPermission Admin = new(Permission.Admin);
    private static readonly OperationPermission ServicesControl = new(Permission.Write, PermissionKeys.ServerServicesControl);
    private static readonly OperationPermission SystemUpgrade = new(Permission.Write, PermissionKeys.ServerSystemUpgrade);
    private static readonly OperationPermission FirewallManage = new(Permission.Write, PermissionKeys.ServerFirewallManage);

    /// <summary>The requirement of <paramref name="kind"/>; null for <see cref="OperationKind.None"/> and for
    /// any value this table does not classify (refused, fail closed).</summary>
    public static OperationPermission? For(OperationKind kind) => s_table.GetValueOrDefault(kind);

    private static readonly FrozenDictionary<OperationKind, OperationPermission> s_table = Build(
        // Docker: the container and compose-project actions and the pull are Write endpoints; the image
        // build and removal, the prune, the compose file deploy and the container exec are Admin ones.
        (Write, [OperationKind.DockerRestartContainer, OperationKind.DockerStartContainer, OperationKind.DockerStopContainer, OperationKind.DockerRemoveContainer, OperationKind.DockerPullImage, OperationKind.DockerUpdateResources, OperationKind.DockerComposeProjectUp, OperationKind.DockerComposeProjectDown, OperationKind.DockerComposeProjectRestart]),
        (Admin, [OperationKind.DockerComposeUp, OperationKind.DockerExecScript, OperationKind.DockerBuildImage, OperationKind.DockerRemoveImage, OperationKind.DockerPrune]),

        // Services and packages (ServersController): named permissions, as on their own endpoints.
        (ServicesControl, [OperationKind.ServiceStart, OperationKind.ServiceStop, OperationKind.ServiceRestart, OperationKind.ServiceInstall, OperationKind.ServiceUninstall]),
        (SystemUpgrade, [OperationKind.SystemPackageUpgrade]),
        (Write, [OperationKind.ServiceStatus, OperationKind.ServiceGetLogs, OperationKind.PackageInventory, OperationKind.PortsObserve]),
        // Enabled only by the configuration deploy, a Server Admin endpoint.
        (Admin, [OperationKind.ServiceEnable]),
        (Admin, [OperationKind.ServerReboot, OperationKind.AgentSelfUpdate]),

        (FirewallManage, [OperationKind.FirewallAllow, OperationKind.FirewallDeny, OperationKind.FirewallDeleteRule, OperationKind.FirewallSetEnabled]),

        // Apache: every endpoint is Write; the proxy and configuration set are queued only by pipelines.
        (Write, [OperationKind.ApacheReload, OperationKind.ApacheTestConfig, OperationKind.ApacheGetLogs, OperationKind.ApacheStart, OperationKind.ApacheStop, OperationKind.ApacheRestart, OperationKind.ApacheSaveConfig, OperationKind.ApacheGetConfig, OperationKind.ApacheGetHtaccess, OperationKind.ApacheSaveHtaccess, OperationKind.ApacheEnableSite, OperationKind.ApacheDisableSite, OperationKind.ApacheDeleteSite, OperationKind.ApacheEnableModule, OperationKind.ApacheDisableModule]),
        (Admin, [OperationKind.ApacheConfigureProxy, OperationKind.ApacheApplyConfigSet]),

        // Certbot: the actions are Write, the certificate creation is Admin.
        (Write, [OperationKind.CertbotRenew, OperationKind.CertbotRenewAll, OperationKind.CertbotDelete, OperationKind.CertbotRevoke, OperationKind.CertbotNormalize, OperationKind.CertbotRenewalCheck, OperationKind.CertbotRemoveDomain]),
        (Admin, [OperationKind.CertbotObtain]),

        (Write, [OperationKind.RkhunterScan, OperationKind.RkhunterUpdate, OperationKind.RkhunterPropupd]),
        (Admin, [OperationKind.RkhunterSetup]),

        // Mail: every endpoint of the mail stack is Write (or Read).
        (Write, [OperationKind.MailStartPostfix, OperationKind.MailStopPostfix, OperationKind.MailRestartPostfix, OperationKind.MailReloadPostfix, OperationKind.MailStartDovecot, OperationKind.MailStopDovecot, OperationKind.MailRestartDovecot, OperationKind.MailReloadDovecot, OperationKind.MailFlushQueue, OperationKind.MailViewQueue, OperationKind.MailTestConfig, OperationKind.MailGetLogs, OperationKind.MailSetup, OperationKind.MailAddDomain, OperationKind.MailAddAccount, OperationKind.MailAddAlias, OperationKind.MailDkimRotate, OperationKind.MailChangePassword, OperationKind.MailRemoveDomain, OperationKind.MailDeleteAccount, OperationKind.MailRemoveAlias, OperationKind.MailDkimRead, OperationKind.MailInstallCertificate, OperationKind.MailSpamInstall, OperationKind.MailSpamConfigure, OperationKind.MailSpamLearn, OperationKind.MailServiceControl, OperationKind.MailSendTest, OperationKind.MailQueueDelete, OperationKind.MailQuotaReport, OperationKind.MailPurge, OperationKind.MailOutboundProbe, OperationKind.MailMasterAccess, OperationKind.MailLeftoversCheck, OperationKind.MailSetQuota, OperationKind.MailRetryTuning]),

        (Write, [OperationKind.PortsentryStart, OperationKind.PortsentryStop, OperationKind.PortsentryRestart, OperationKind.PortsentryStatus, OperationKind.PortsentryGetLogs, OperationKind.PortsentryUnblock]),
        (Admin, [OperationKind.PortsentrySetup]),

        (Write, [OperationKind.CronSave, OperationKind.CronDelete]),

        // TeamSpeak: start, stop, restart, graceful restart and logs are Write or Read endpoints; the setup
        // is Admin, and the raw ServerQuery line also carries the Admin token and snapshot commands.
        (Write, [OperationKind.TeamspeakStart, OperationKind.TeamspeakStop, OperationKind.TeamspeakRestart, OperationKind.TeamspeakGracefulRestart, OperationKind.TeamspeakGetLogs]),
        (Admin, [OperationKind.TeamspeakSetup, OperationKind.TeamspeakServerQuery]),

        // Queued by the project deletion flow, a Project Admin endpoint.
        (Admin, [OperationKind.ProjectResourcesDiscover, OperationKind.ProjectResourcesRemove]),

        // Queued only by pipeline runs, blue-green steps, backup policies and AI runs, whose own paths carry
        // the context (owner authorization, environment, run binding) this endpoint does not.
        (Admin, [OperationKind.PipelineCollectArtifacts, OperationKind.PipelineCreateRelease, OperationKind.PipelineSubstituteVariables, OperationKind.PipelinePublishCoverage, OperationKind.PipelinePublishLint, OperationKind.PipelinePublishComplexity, OperationKind.PipelineDeploy, OperationKind.PipelineRestoreArtifacts, OperationKind.PipelineRunScanner, OperationKind.PipelineEvaluateAnalysisGate, OperationKind.PipelinePublishObservabilityBundle, OperationKind.AiRun, OperationKind.PipelineSmoke, OperationKind.PipelineDotnetTest, OperationKind.PipelineGateStatus, OperationKind.PipelinePublishMutation, OperationKind.PipelineFunction, OperationKind.PipelineDeploymentFunction]),
        (Admin, [OperationKind.BlueGreenMigrate, OperationKind.BlueGreenUp, OperationKind.BlueGreenSwitch, OperationKind.BlueGreenCommit, OperationKind.BlueGreenRollback, OperationKind.BlueGreenRetire, OperationKind.BlueGreenRevert]),
        (Admin, [OperationKind.BackupExecute, OperationKind.BackupRestoreCheck, OperationKind.BackupRestore])
    );

    // A kind listed twice is a classification error, refused at type initialization.
    private static FrozenDictionary<OperationKind, OperationPermission> Build(
        params (OperationPermission Required, OperationKind[] Kinds)[] groups)
    {
        var table = new Dictionary<OperationKind, OperationPermission>();
        foreach (var (required, kinds) in groups)
            foreach (var kind in kinds)
                table.Add(kind, required);
        return table.ToFrozenDictionary();
    }
}
