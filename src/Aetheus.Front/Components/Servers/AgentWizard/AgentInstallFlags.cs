// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Servers.AgentWizard;

/// <summary>
/// S-TECH-WZFL: single source of truth for the agent installer module/capability flags emitted by the
/// Add-Agent wizard. Both the install step (<see cref="WizardInstallInstructions"/>, which shows the
/// command) and the "copy all commands" summary (<see cref="AddAgent"/>) built the identical flag string
/// independently and could drift; they now both call here.
/// </summary>
internal static class AgentInstallFlags
{
    /// <summary>
    /// Builds the Linux <c>install-agent-linux.sh</c> module/capability flag suffix. Default
    /// (pipeline-runner on, everything else off) emits no flags - the cleanest command; each capability
    /// is emitted with least privilege (a deploy host gets <c>--module deployment --enable-certbot-manage</c>,
    /// not the full server-management bundle; standalone Docker only when server-management is not selected).
    /// </summary>
    public static string BuildLinux(
        bool includePipelineRunner,
        bool includeServerManagement,
        bool includeDeploymentAgent,
        bool includePatchManagement,
        bool includeFirewallManagement,
        bool includeDocker,
        bool includeHostReboot = false)
    {
        var flags = string.Empty;
        if (!includePipelineRunner) flags += " --no-pipeline-runner";
        if (includeServerManagement) flags += " --module server-management";
        // Deploy target for the release pipelines: the deployment module (web root) PLUS certbot-manage,
        // which issues the app/vitrine HTTPS certs AND implies apache-manage for the reverse-proxy vhosts.
        // certbot-manage is precisely what --module server-management does NOT grant, so a deploy host must
        // emit it explicitly (least privilege: a working deploy host needs no server-management bundle).
        if (includeDeploymentAgent) flags += " --module deployment --enable-certbot-manage";
        // ADR-024 4.1: patch-manage is an independent opt-in (broad apt-get upgrade grant), never bundled.
        if (includePatchManagement) flags += " --enable-patch-manage";
        // ADR-024 4.2: firewall-manage is an independent opt-in (ufw helper).
        if (includeFirewallManagement) flags += " --enable-firewall-manage";
        // Recette R-8: host-reboot is an independent opt-in (argv-exact `systemctl reboot` grant), so Aetheus can
        // run the host reboots it schedules; never bundled with server-management.
        if (includeHostReboot) flags += " --enable-host-reboot";
        // --enable-docker (docker-group membership) is implied by server-management, so only add it
        // standalone when the operator wants container isolation WITHOUT the full server-admin posture.
        if (includeDocker && !includeServerManagement) flags += " --enable-docker";
        return flags;
    }

    /// <summary>Builds the Windows <c>install-agent-windows.ps1</c> flag suffix.</summary>
    public static string BuildWindows(bool includePipelineRunner)
        => includePipelineRunner ? string.Empty : " -NoPipelineRunner";
}
