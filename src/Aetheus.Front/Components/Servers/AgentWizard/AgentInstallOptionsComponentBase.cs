// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Servers.AgentWizard;

public abstract class AgentInstallOptionsComponentBase : ComponentBase
{
    [Parameter] public string Platform { get; set; } = "linux";
    [Parameter] public string ServerBaseUrl { get; set; } = string.Empty;
    [Parameter] public string SiteVersion { get; set; } = "dev";
    [Parameter] public bool IncludePipelineRunner { get; set; } = true;
    [Parameter] public bool IncludeServerManagement { get; set; }
    [Parameter] public bool IncludeDeploymentAgent { get; set; }
    [Parameter] public bool IncludePatchManagement { get; set; }
    [Parameter] public bool IncludeFirewallManagement { get; set; }
    [Parameter] public bool IncludeHostReboot { get; set; }
    [Parameter] public bool IncludeDocker { get; set; }
    [Parameter] public bool DevModeInsecureTls { get; set; }

    protected bool IsLinux => string.Equals(Platform, "linux", StringComparison.OrdinalIgnoreCase);
}
