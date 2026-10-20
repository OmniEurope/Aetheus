// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>PLAN-010 lot 1: the containers tab shows each container's host access, names the Aetheus
/// containers that need some of it, and says when those settings are unknown instead of showing nothing.</summary>
public sealed class DockerContainerRiskBadgesTests : BunitContext
{
    public DockerContainerRiskBadgesTests() => BunitTestHelper.RegisterServices(this);

    private static DockerContainerDto Container(
        string name,
        DockerContainerRisks risks = DockerContainerRisks.None,
        AetheusContainerRole role = AetheusContainerRole.None,
        string caps = "",
        bool inspected = true) => new()
        {
            ContainerId = (name + "000000000000")[..12],
            Name = name,
            Image = "img:1",
            State = "running",
            Status = "Up",
            SecurityInspected = inspected,
            Risks = risks,
            AetheusRole = role,
            DangerousCapabilities = caps
        };

    private IRenderedComponent<DockerContainerRiskBadges> RenderBadges(DockerContainerDto container) =>
        Render<DockerContainerRiskBadges>(p => p.Add(x => x.Container, container));

    private IRenderedComponent<DockerContainersTab> RenderTab(params DockerContainerDto[] containers) =>
        Render<DockerContainersTab>(p => p
            .Add(x => x.Server, new ServerDetailDto
            {
                Id = 10,
                Name = "docker-server",
                Hostname = "10.0.0.1",
                Type = ServerType.Docker,
                Status = ServerStatus.Online,
                Tags = [],
                Services = [],
                Docker = new DockerDataDto { Containers = [.. containers] }
            })
            .Add(x => x.ServerId, 10)
            .Add(x => x.InitialLoaded, true));

    [Fact]
    public void UnexplainedFlags_AreRedBadges_OnePerFlag()
    {
        var cut = RenderBadges(Container("probe", DockerContainerRisks.Privileged | DockerContainerRisks.DockerSocket
            | DockerContainerRisks.DangerousCapabilities, caps: "SYS_ADMIN"));

        var badges = cut.FindComponents<OmniBadge>();
        Assert.Equal(3, badges.Count);
        Assert.All(badges, b => Assert.Equal(OmniTone.Danger, b.Instance.Tone));
        Assert.Equal(
            ["Privileged", "DockerSocket", "DangerousCapabilities"],
            cut.FindAll("[data-docker-risk]").Select(e => e.GetAttribute("data-docker-risk")));
        Assert.Contains("DockerRiskCapabilities", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void AetheusBuildKitBuilder_IsNamed_ItsPrivilegeIsExpected_AnExtraSocketStaysRed()
    {
        var cut = RenderBadges(Container("buildx_buildkit_aetheus-a0",
            DockerContainerRisks.Privileged | DockerContainerRisks.DockerSocket, AetheusContainerRole.BuildKitBuilder));

        Assert.Equal("BuildKitBuilder", cut.Find("[data-docker-role]").GetAttribute("data-docker-role"));
        Assert.Contains("DockerRoleBuildKitBuilder", cut.Find("[data-docker-role]").TextContent, StringComparison.Ordinal);
        Assert.Equal("true", cut.Find("[data-docker-risk=Privileged]").GetAttribute("data-expected"));
        Assert.Equal("false", cut.Find("[data-docker-risk=DockerSocket]").GetAttribute("data-expected"));
        var tones = cut.FindComponents<OmniBadge>().Select(b => b.Instance.Tone).ToList();
        Assert.Equal([OmniTone.Info, OmniTone.Neutral, OmniTone.Danger], tones);
    }

    [Fact]
    public void CleanOrUninspectedContainer_DrawsNoBadge()
    {
        Assert.Empty(RenderBadges(Container("web")).FindComponents<OmniBadge>());
        Assert.Empty(RenderBadges(Container("old", DockerContainerRisks.Privileged, inspected: false)).FindComponents<OmniBadge>());
    }

    [Fact]
    public void Tab_CountsTheContainersWhoseHostAccessIsUnexplained()
    {
        var cut = RenderTab(
            Container("web"),
            Container("probe", DockerContainerRisks.DockerSocket),
            Container("buildx_buildkit_aetheus-a0", DockerContainerRisks.Privileged, AetheusContainerRole.BuildKitBuilder));

        var alert = cut.FindComponent<OmniAlert>();
        Assert.Equal(OmniSeverity.Danger, alert.Instance.Severity);
        Assert.Equal("DockerRiskSummaryTitle", alert.Instance.Title);
        Assert.Contains("DockerRiskSummary", cut.Find(".docker-risk-summary").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".docker-risk-unknown"));
    }

    [Fact]
    public void Tab_InFrench_ReadsTheCountAndNamesTheBuilder()
    {
        Services.AddSingleton<IStringLocalizer<Aetheus.Front.Resources.AppStrings>>(ResxLocalizer.French());
        var cut = RenderTab(
            Container("probe", DockerContainerRisks.DockerSocket),
            Container("other", DockerContainerRisks.HostPid),
            Container("buildx_buildkit_aetheus-a0", DockerContainerRisks.Privileged, AetheusContainerRole.BuildKitBuilder));

        Assert.Contains("2 conteneur(s) peuvent atteindre l'hôte.", cut.Find(".docker-risk-summary").TextContent, StringComparison.Ordinal);
        var badges = RenderBadges(Container("buildx_buildkit_aetheus-a0", DockerContainerRisks.Privileged, AetheusContainerRole.BuildKitBuilder));
        Assert.Equal("Aetheus : constructeur BuildKit", badges.Find("[data-docker-role]").TextContent.Trim());
        Assert.Equal("Privilégié", badges.Find("[data-docker-risk=Privileged]").TextContent.Trim());
    }

    [Fact]
    public void Tab_WithNothingInspected_SaysThePrivilegesAreUnknown_NeverClean()
    {
        var cut = RenderTab(Container("web", inspected: false), Container("db", inspected: false));

        Assert.Contains("DockerRiskUnknown", cut.Find(".docker-risk-unknown").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".docker-risk-summary"));
    }

    [Fact]
    public void Tab_AllInspectedAndClean_ShowsNoBanner()
    {
        var cut = RenderTab(Container("web"), Container("db"));

        Assert.Empty(cut.FindAll(".docker-risk-summary"));
        Assert.Empty(cut.FindAll(".docker-risk-unknown"));
    }
}
