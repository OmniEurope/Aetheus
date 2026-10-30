// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>R-113: Docker's English status text reaches the user in French.</summary>
public sealed class DockerContainerStatusTextTests : BunitContext
{
    private static readonly ResxLocalizer French = ResxLocalizer.French();

    [Theory]
    [InlineData("Created", "Créé")]
    [InlineData("Up 2 hours", "En marche depuis 2 heures")]
    [InlineData("Up 2 hours (healthy)", "En marche depuis 2 heures (sain)")]
    [InlineData("Up 2 hours (unhealthy)", "En marche depuis 2 heures (non sain)")]
    [InlineData("Up 5 minutes (health: starting)", "En marche depuis 5 minutes (contrôle de santé en cours)")]
    [InlineData("Exited (0) 3 minutes ago", "Arrêté (code 0) il y a 3 minutes")]
    [InlineData("Exited (137) 1 second ago", "Arrêté (code 137) il y a 1 seconde")]
    [InlineData("Restarting (1) 5 seconds ago", "Redémarrage (code 1) il y a 5 secondes")]
    [InlineData("Up About an hour", "En marche depuis environ une heure")]
    [InlineData("Up About a minute", "En marche depuis environ une minute")]
    [InlineData("Up Less than a second", "En marche depuis moins d'une seconde")]
    [InlineData("Up 3 days (Paused)", "En marche depuis 3 jours (en pause)")]
    [InlineData("Up 2 weeks", "En marche depuis 2 semaines")]
    [InlineData("Up 4 months", "En marche depuis 4 mois")]
    [InlineData("Up 1 year", "En marche depuis 1 an")]
    [InlineData("Exited (0) About an hour ago", "Arrêté (code 0) il y a environ une heure")]
    [InlineData("Paused", "En pause")]
    [InlineData("Dead", "Défaillant")]
    [InlineData("Removal In Progress", "Suppression en cours")]
    public void A_Docker_Status_Reads_In_French(string docker, string expected) =>
        Assert.Equal(expected, DockerContainerStatusText.Format(docker, French));

    [Theory]
    [InlineData("Something Docker adds later")]
    [InlineData("Up forever")]
    [InlineData("Exited (x) 3 minutes ago")]
    public void An_Unknown_Status_Is_Shown_As_Docker_Wrote_It(string docker) =>
        Assert.Equal(docker, DockerContainerStatusText.Format(docker, French));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_Empty_Status_Stays_Empty_And_Never_Throws(string docker) =>
        Assert.Equal(docker, DockerContainerStatusText.Format(docker, French));

    [Fact]
    public void The_Neutral_Resources_Read_In_English()
    {
        var english = ResxLocalizer.English();
        Assert.Equal("Up for 2 hours", DockerContainerStatusText.Format("Up 2 hours", english));
        Assert.Equal("Exited (code 0) 3 minutes ago", DockerContainerStatusText.Format("Exited (0) 3 minutes ago", english));
    }

    [Fact]
    public void The_Containers_Grid_Renders_The_Status_In_French()
    {
        var handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        Services.AddSingleton<IStringLocalizer<Aetheus.Front.Resources.AppStrings>>(French);
        handler.SetJsonResponse("api/servers/10/docker/containers", new List<DockerContainerDto>());
        var server = new ServerDetailDto
        {
            Id = 10,
            Name = "docker-server",
            Hostname = "10.0.0.1",
            Type = ServerType.Docker,
            Status = ServerStatus.Online,
            Tags = [],
            Services = [],
            Docker = new DockerDataDto
            {
                Containers =
                [
                    new DockerContainerDto { ContainerId = "aaa111", Name = "web", Image = "nginx", State = "running", Status = "Up 2 hours (healthy)" },
                    new DockerContainerDto { ContainerId = "bbb222", Name = "job", Image = "alpine", State = "exited", Status = "Exited (0) 3 minutes ago" }
                ],
                Images = [],
                ComposeStacks = [],
                Networks = [],
                Volumes = []
            }
        };

        var cut = Render<DockerContainersTab>(p => p
            .Add(x => x.Server, server)
            .Add(x => x.ServerId, 10)
            .Add(x => x.InitialLoaded, true));

        var text = cut.Markup;
        Assert.Contains("En marche depuis 2 heures (sain)", text);
        Assert.Contains("Arrêté (code 0) il y a 3 minutes", text);
        Assert.DoesNotContain("Up 2 hours", text);
        Assert.DoesNotContain("3 minutes ago", text);
    }

    [Theory]
    [InlineData("running(2)", "En marche (2)")]
    [InlineData("exited(1)", "Arrêté (1)")]
    [InlineData("running(1), exited(1)", "En marche (1), arrêté (1)")]
    [InlineData("restarting(1)", "En redémarrage (1)")]
    [InlineData("paused(1)", "En pause (1)")]
    [InlineData("created(1)", "Créé (1)")]
    [InlineData("dead(1)", "Défaillant (1)")]
    public void A_Compose_Stack_Status_Reads_In_French(string compose, string expected) =>
        Assert.Equal(expected, DockerContainerStatusText.FormatCompose(compose, French));

    [Theory]
    [InlineData("")]
    [InlineData("running")]
    [InlineData("running(1), flying(2)")]
    [InlineData("Something new")]
    public void An_Unknown_Compose_Status_Is_Shown_As_Docker_Wrote_It(string compose) =>
        Assert.Equal(compose, DockerContainerStatusText.FormatCompose(compose, French));

    [Fact]
    public void The_Compose_Grid_Renders_The_Stack_Status_In_French()
    {
        var handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        Services.AddSingleton<IStringLocalizer<Aetheus.Front.Resources.AppStrings>>(French);

        var cut = Render<DockerComposeTab>(p => p
            .Add(x => x.ServerId, 10)
            .Add(x => x.Stacks, new List<DockerComposeStackDto>
            {
                new() { Name = "web", Status = "running(2)", RunningCount = 2, TotalCount = 2 },
                new() { Name = "mixed", Status = "running(1), exited(1)", RunningCount = 1, TotalCount = 2 }
            }));

        var text = cut.Markup;
        Assert.Contains("En marche (2)", text);
        Assert.Contains("En marche (1), arrêté (1)", text);
        Assert.DoesNotContain("running(", text);
    }
}
