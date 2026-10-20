// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary>Badges of a container's host-escape settings and of the Aetheus role that explains some of them.</summary>
public partial class DockerContainerRiskBadges
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public DockerContainerDto Container { get; set; } = default!;

    private sealed record Flag(DockerContainerRisks Risk, string Label, string Hint, bool Expected);

    private string? RoleLabel => Container.AetheusRole switch
    {
        AetheusContainerRole.BuildKitBuilder => L["DockerRoleBuildKitBuilder"].Value,
        AetheusContainerRole.BrowserSmoke => L["DockerRoleBrowserSmoke"].Value,
        AetheusContainerRole.VpsSimulator => L["DockerRoleVpsSimulator"].Value,
        _ => null
    };

    private IEnumerable<Flag> Flags
    {
        get
        {
            var expected = DockerContainerSecurity.ExpectedRisks(Container.AetheusRole);
            foreach (var (risk, label, hint) in new (DockerContainerRisks, string, string)[]
            {
                (DockerContainerRisks.Privileged, L["DockerRiskPrivileged"], L["DockerRiskPrivilegedHint"]),
                (DockerContainerRisks.DockerSocket, L["DockerRiskDockerSocket"], L["DockerRiskDockerSocketHint"]),
                (DockerContainerRisks.HostPid, L["DockerRiskHostPid"], L["DockerRiskHostPidHint"]),
                (DockerContainerRisks.HostNetwork, L["DockerRiskHostNetwork"], L["DockerRiskHostNetworkHint"]),
                (DockerContainerRisks.DangerousCapabilities,
                    L["DockerRiskCapabilities", Container.DangerousCapabilities], L["DockerRiskCapabilitiesHint"])
            })
            {
                if (Container.Risks.HasFlag(risk))
                    yield return new Flag(risk, label, hint, expected.HasFlag(risk));
            }
        }
    }
}
