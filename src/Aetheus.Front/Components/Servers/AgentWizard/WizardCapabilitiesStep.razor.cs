// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Servers.AgentWizard;

public partial class WizardCapabilitiesStep
{
    [Parameter] public string Platform { get; set; } = "linux";
    [Parameter] public bool IncludePipelineRunner { get; set; }
    [Parameter] public EventCallback<bool> IncludePipelineRunnerChanged { get; set; }
    [Parameter] public bool IncludeServerManagement { get; set; }
    [Parameter] public EventCallback<bool> IncludeServerManagementChanged { get; set; }
    [Parameter] public bool IncludeDeploymentAgent { get; set; }
    [Parameter] public EventCallback<bool> IncludeDeploymentAgentChanged { get; set; }
    [Parameter] public bool IncludePatchManagement { get; set; }
    [Parameter] public EventCallback<bool> IncludePatchManagementChanged { get; set; }
    [Parameter] public bool IncludeFirewallManagement { get; set; }
    [Parameter] public EventCallback<bool> IncludeFirewallManagementChanged { get; set; }
    [Parameter] public bool IncludeHostReboot { get; set; }
    [Parameter] public EventCallback<bool> IncludeHostRebootChanged { get; set; }
    [Parameter] public bool IncludeDocker { get; set; }
    [Parameter] public EventCallback<bool> IncludeDockerChanged { get; set; }
    [Parameter] public bool DevModeInsecureTls { get; set; }
    [Parameter] public EventCallback<bool> DevModeInsecureTlsChanged { get; set; }

    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    private bool IsLinux => string.Equals(Platform, "linux", StringComparison.OrdinalIgnoreCase);

    // --- Docker card state ---
    // Checked when explicitly enabled or pulled in by server-management.
    private bool DockerChecked => IncludeDocker;
    // Container isolation is a pipeline feature: toggleable only when the pipeline-runner module is
    // on, and disabled (forced off) otherwise. Independent of server-management - you can manage a
    // server's Docker service without opting pipelines into container isolation.
    private bool DockerLocked => !IncludePipelineRunner;
    private bool DockerRequiresRunner => !IncludePipelineRunner;

    // No actionable module selected → the agent has no role (blocked when leaving this step). A
    // deployment-only host (just a deploy target, no CI/CD runner, no server admin) is a valid role.
    private bool NoActionableModule => !IncludePipelineRunner && !IncludeServerManagement && !IncludeDeploymentAgent && !IncludePatchManagement && !IncludeFirewallManagement && !IncludeHostReboot;
}
