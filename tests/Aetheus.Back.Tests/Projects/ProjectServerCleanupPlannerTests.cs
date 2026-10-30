// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Projects;

namespace Aetheus.Back.Tests.Projects;

/// <summary> Recette R-97 (ADR-058): what a project deployed, read from its variable libraries the way the
/// generic production template writes them, and which servers are asked about it. </summary>
public sealed class ProjectServerCleanupPlannerTests
{
    private const string Capable = "{\"software\":[\"project.resources\"]}";

    private static readonly CleanupServerInfo Prod = new(3, "vps-prod", 1, ServerStatus.Online, Capable, false);
    private static readonly CleanupServerInfo Qa = new(4, "vps-qa", 1, ServerStatus.Online, Capable, false);
    private static readonly CleanupServerInfo Foreign = new(9, "other-org", 2, ServerStatus.Online, Capable, false);

    private static readonly Dictionary<string, string> Share = new()
    {
        ["APP_SUBDOMAIN"] = "app",
        ["API_SUBDOMAIN"] = "api",
        ["CERTBOT_EMAIL"] = "ops@example.com",
    };

    private static ProjectServerCleanupInputs Inputs(
        IReadOnlyList<CleanupLibrary> libraries,
        IReadOnlyList<CleanupEnvironment>? environments = null,
        IReadOnlyList<CleanupBackupPolicy>? policies = null,
        IReadOnlyList<CleanupForeignProject>? others = null) =>
        new(7, "Portfolio", 1, libraries,
            environments ?? [new CleanupEnvironment(21, "prod", [Prod]), new CleanupEnvironment(22, "qa", [Qa])],
            [], policies ?? [], others ?? []);

    private static CleanupLibrary Library(string name, Dictionary<string, string> entries) => new(name, null, null, entries);

    private static readonly ProjectServerCleanupGuards Unguarded = new(ServerWriteAccess.All, new HashSet<string>());

    private static ProjectServerCleanupPlan Plan(ProjectServerCleanupInputs inputs, ProjectServerCleanupGuards? guards = null) =>
        ProjectServerCleanupPlanner.Build(inputs, guards ?? Unguarded);

    [Fact]
    public void AnEmptyApplicationSubdomain_NamesTheDomainItself_AndAnAbsentOneNamesNoHost()
    {
        var empty = ProjectServerCleanupLibraryValues.RawHosts(new Dictionary<string, string>
        {
            ["APP_SUBDOMAIN"] = "",
            ["API_SUBDOMAIN"] = "api",
            ["DOMAIN"] = "w7.example.com",
        }).ToDictionary(host => host.HostKey, host => host.Raw);
        var absent = ProjectServerCleanupLibraryValues.RawHosts(new Dictionary<string, string>
        {
            ["API_SUBDOMAIN"] = "",
            ["DOMAIN"] = "w7.example.com",
        }).ToDictionary(host => host.HostKey, host => host.Raw);

        Assert.Equal("$(HOST_PREFIX:-)$(DOMAIN)", empty["APP_HOST"]);
        Assert.Equal("$(HOST_PREFIX:-)$(API_SUBDOMAIN).$(DOMAIN)", empty["API_HOST"]);
        Assert.Null(absent["APP_HOST"]);
        Assert.Null(absent["API_HOST"]);
    }

    [Fact]
    public void AServerTheUserMayNotWriteTo_IsNeverAsked_AndSaidSo_WhileTheOthersAre()
    {
        var plan = Plan(Inputs(
            [Library("portfolio.prod", new() { ["COMPOSE_PROJECT"] = "portfolio-prod", ["APP_HOST"] = "app.portfolio.example" }),
             Library("portfolio.qa", new() { ["COMPOSE_PROJECT"] = "portfolio-qa" })],
            policies: [new CleanupBackupPolicy(31, Prod)]),
            new ProjectServerCleanupGuards(new ServerWriteAccess(new HashSet<int> { Qa.Id }), new HashSet<string>()));

        var prod = Assert.Single(plan.Servers, server => server.ServerId == Prod.Id);
        Assert.Equal(ProjectDeletionServerState.NoWriteAccess, prod.State);
        Assert.Empty(prod.MissingCapabilities);
        Assert.Equal(ProjectDeletionServerState.Ready, Assert.Single(plan.Servers, server => server.ServerId == Qa.Id).State);
        Assert.Equal(ProjectDeletionServerState.NoWriteAccess,
            Assert.Single(Plan(Inputs([Library("portfolio.prod", new() { ["COMPOSE_PROJECT"] = "portfolio-prod" })]),
                new ProjectServerCleanupGuards(ServerWriteAccess.None, new HashSet<string>())).Servers).State);
    }

    [Fact]
    public void APublicHostOfTheControlPlane_IsRefused_WhateverTheProjectsName()
    {
        var plan = Plan(Inputs([Library("portfolio.prod", new() { ["COMPOSE_PROJECT"] = "portfolio-prod", ["APP_HOST"] = "Deploy.Example.com", ["API_HOST"] = "api.portfolio.example" })]),
            new ProjectServerCleanupGuards(ServerWriteAccess.All, new HashSet<string> { "deploy.example.com" }));

        var refused = Assert.Single(plan.Refused);
        Assert.Equal(("APP_HOST", "deploy.example.com", ProjectDeletionRefusal.ControlPlaneHost), (refused.Key, refused.Value, refused.Reason));
        Assert.Equal(["api.portfolio.example"], Assert.Single(Assert.Single(plan.Servers).Environments).Hosts);
    }

    [Fact]
    public void TheTemplateLibraries_GiveEachEnvironmentItsComposeProject_Hosts_AndStateDirectory_OnItsServer()
    {
        var plan = Plan(Inputs(
        [
            Library("portfolio.share", Share),
            Library("portfolio.prod", new() { ["DOMAIN"] = "portfolio.example", ["COMPOSE_PROJECT"] = "portfolio-prod", ["STATE_DIR"] = "/var/lib/portfolio-production" }),
            Library("portfolio.qa", new() { ["DOMAIN"] = "portfolio.example", ["HOST_PREFIX"] = "qa-", ["COMPOSE_PROJECT"] = "portfolio-qa" }),
        ]));

        Assert.Empty(plan.Refused);
        var prod = Assert.Single(plan.Servers, server => server.ServerId == 3);
        var prodEnvironment = Assert.Single(prod.Environments);
        Assert.Equal("prod", prodEnvironment.Name);
        Assert.Equal("portfolio-prod", prodEnvironment.ComposeProject);
        Assert.Equal(["app.portfolio.example", "api.portfolio.example"], prodEnvironment.Hosts);
        Assert.Equal("/var/lib/portfolio-production", prodEnvironment.StateDirectory);
        // R-85: the repository copy every deployment writes is announced with the path the templates use.
        Assert.Equal("/var/lib/aetheus-agent/.deployments/portfolio-prod", prodEnvironment.DeploymentDirectory);
        Assert.Equal(ProjectDeletionServerState.Ready, prod.State);
        var qa = Assert.Single(Assert.Single(plan.Servers, server => server.ServerId == 4).Environments);
        Assert.Equal(["qa-app.portfolio.example", "qa-api.portfolio.example"], qa.Hosts);
        Assert.Null(qa.StateDirectory);
    }

    [Fact]
    public void TheDeploymentDirectory_IsAskedAboutAndConfirmedThroughTheComposeProject_AndBelongsToItsEnvironment()
    {
        var plan = Plan(Inputs(
            [Library("portfolio.prod", new() { ["COMPOSE_PROJECT"] = "portfolio-prod" })]));

        var prod = Assert.Single(plan.Servers, server => server.ServerId == 3);
        var directory = new ProjectServerResourceRef(ProjectServerResourceKind.DeploymentDirectory,
            "/var/lib/aetheus-agent/.deployments/portfolio-prod", "portfolio-prod");
        Assert.Null(prod.Spec().Validate());
        Assert.True(prod.Spec().Covers(directory));
        Assert.False(prod.Spec().Covers(directory with { Scope = "portfolio-qa" }));
        Assert.Null(prod.Spec([directory]).Validate());
        Assert.Equal("prod", prod.EnvironmentOf(directory.Kind, directory.Scope));
    }

    [Fact]
    public void AnEnvironmentWithoutComposeProject_AnnouncesNoDeploymentDirectory()
    {
        var plan = Plan(Inputs([Library("portfolio.prod", new() { ["APP_HOST"] = "app.portfolio.example" })]));

        Assert.Null(Assert.Single(Assert.Single(plan.Servers, server => server.ServerId == 3).Environments).DeploymentDirectory);
    }

    [Fact]
    public void ExplicitHosts_WinOverTheFormula_AndAreReadInLowerCase()
    {
        var plan = Plan(Inputs(
        [
            Library("portfolio.share", Share),
            Library("portfolio.production", new() { ["DOMAIN"] = "portfolio.example", ["APP_HOST"] = "WWW.Portfolio.Example", ["API_HOST"] = "$(API_SUBDOMAIN).$(DOMAIN)" }),
        ]));

        Assert.Equal(["www.portfolio.example", "api.portfolio.example"], Assert.Single(Assert.Single(plan.Servers).Environments).Hosts);
    }

    [Fact]
    public void AnEnvironmentOwnedLibrary_IsReadForThatEnvironment_WhateverItsName()
    {
        var plan = Plan(Inputs(
            [new CleanupLibrary("anything", 22, null, new Dictionary<string, string> { ["COMPOSE_PROJECT"] = "portfolio-qa" })]));

        var server = Assert.Single(plan.Servers);
        Assert.Equal(4, server.ServerId);
        Assert.Equal("qa", Assert.Single(server.Environments).Name);
    }

    [Theory]
    [InlineData("COMPOSE_PROJECT", "w6-prod;rm -rf /", ProjectDeletionRefusal.InvalidName)]
    [InlineData("COMPOSE_PROJECT", "w6-prod\nrm", ProjectDeletionRefusal.InvalidName)]
    [InlineData("COMPOSE_PROJECT", "w6-prod`id`", ProjectDeletionRefusal.InvalidName)]
    [InlineData("COMPOSE_PROJECT", "$(whoami)", ProjectDeletionRefusal.UnresolvedReference)]
    [InlineData("COMPOSE_PROJECT", "$(UNDEFINED)-prod", ProjectDeletionRefusal.UnresolvedReference)]
    [InlineData("COMPOSE_PROJECT", "Portfolio Prod", ProjectDeletionRefusal.InvalidName)]
    [InlineData("APP_HOST", "app portfolio.example", ProjectDeletionRefusal.InvalidName)]
    [InlineData("APP_HOST", "app/../portfolio.example", ProjectDeletionRefusal.InvalidName)]
    [InlineData("APP_HOST", "*.portfolio.example", ProjectDeletionRefusal.InvalidName)]
    [InlineData("APP_HOST", "localhost", ProjectDeletionRefusal.InvalidName)]
    [InlineData("STATE_DIR", "/", ProjectDeletionRefusal.InvalidName)]
    [InlineData("STATE_DIR", "var/lib/portfolio", ProjectDeletionRefusal.InvalidName)]
    [InlineData("STATE_DIR", "/var/lib/portfolio/../docker", ProjectDeletionRefusal.InvalidName)]
    [InlineData("STATE_DIR", "/var/lib/..", ProjectDeletionRefusal.InvalidName)]
    [InlineData("STATE_DIR", "/var/lib/docker", ProjectDeletionRefusal.InvalidName)]
    [InlineData("STATE_DIR", "/var/lib/aetheus-agent", ProjectDeletionRefusal.InvalidName)]
    [InlineData("STATE_DIR", "/var/lib/aetheus-agent-backups", ProjectDeletionRefusal.InvalidName)]
    [InlineData("STATE_DIR", "/etc/portfolio", ProjectDeletionRefusal.InvalidName)]
    [InlineData("STATE_DIR", "/var/lib/portfolio/data", ProjectDeletionRefusal.InvalidName)]
    [InlineData("STATE_DIR", "/var/lib/other-production", ProjectDeletionRefusal.OutsideProjectBase)]
    [InlineData("STATE_DIR", "/var/lib/portfolios", ProjectDeletionRefusal.OutsideProjectBase)]
    [InlineData("STATE_DIR", "/var/lib/aetheus-agent/keys", ProjectDeletionRefusal.InvalidName)]
    [InlineData("STATE_DIR", "/var/lib/aetheus-agent/w", ProjectDeletionRefusal.InvalidName)]
    [InlineData("STATE_DIR", "/var/lib/aetheus-agent/projects/../keys", ProjectDeletionRefusal.InvalidName)]
    [InlineData("STATE_DIR", "/var/lib/aetheus-agent/projects/portfolio-production/sub", ProjectDeletionRefusal.InvalidName)]
    [InlineData("STATE_DIR", "/var/lib/aetheus-agent/projects/aetheus-production", ProjectDeletionRefusal.ReservedForControlPlane)]
    [InlineData("STATE_DIR", "/var/lib/aetheus-agent/projects/other-production", ProjectDeletionRefusal.OutsideProjectBase)]
    public void AValueThatIsNotAStrictName_IsRefusedAndListed_NeverUsed(string key, string value, ProjectDeletionRefusal reason)
    {
        var plan = Plan(Inputs([Library("portfolio.prod", new() { [key] = value })]));

        var refused = Assert.Single(plan.Refused);
        Assert.Equal(("prod", key, reason), (refused.Environment, refused.Key, refused.Reason));
        Assert.Empty(plan.Servers);
    }

    [Fact]
    public void AnEmptyStateDirectory_IsIgnored_NotTakenForTheRoot()
    {
        var plan = Plan(Inputs(
            [Library("portfolio.prod", new() { ["STATE_DIR"] = "", ["COMPOSE_PROJECT"] = "portfolio-prod" })]));

        Assert.Empty(plan.Refused);
        Assert.Null(Assert.Single(Assert.Single(plan.Servers).Environments).StateDirectory);
    }

    [Fact]
    public void AValueAnotherProjectAlsoNames_IsRefused_WhateverItsCasing_OrTheExpressionThatProducesIt()
    {
        // The other project writes the same names through its own share library and in another casing.
        var shop = new CleanupForeignProject("shop", false,
        [
            Library("shop.share", new() { ["BASE"] = "PORTFOLIO", ["APP_SUBDOMAIN"] = "App" }),
            Library("shop.prod", new() { ["COMPOSE_PROJECT"] = "$(BASE)-Prod", ["DOMAIN"] = "Portfolio.Example" }),
        ]);
        var plan = Plan(Inputs(
            [Library("portfolio.share", Share),
             Library("portfolio.prod", new() { ["COMPOSE_PROJECT"] = "portfolio-prod", ["DOMAIN"] = "portfolio.example", ["STATE_DIR"] = "/var/lib/portfolio" })],
            others: [shop]));

        Assert.Equal(
            [("COMPOSE_PROJECT", "portfolio-prod"), ("APP_HOST", "app.portfolio.example")],
            plan.Refused.Where(item => item.Reason == ProjectDeletionRefusal.SharedWithAnotherProject).Select(item => (item.Key, item.Value)));
        var environment = Assert.Single(Assert.Single(plan.Servers).Environments);
        Assert.Null(environment.ComposeProject);
        Assert.Equal(["api.portfolio.example"], environment.Hosts);
        Assert.Equal("/var/lib/portfolio", environment.StateDirectory);
    }

    [Fact]
    public void AComposeProjectNotNamedAfterTheProject_IsListed_NeverRemoved()
    {
        var plan = Plan(Inputs(
            [Library("portfolio.prod", new() { ["COMPOSE_PROJECT"] = "shared-prod", ["STATE_DIR"] = "/var/lib/portfolio" })]));

        var refused = Assert.Single(plan.Refused);
        Assert.Equal(("COMPOSE_PROJECT", "shared-prod", ProjectDeletionRefusal.NotNamedAfterProject), (refused.Key, refused.Value, refused.Reason));
        Assert.Null(Assert.Single(Assert.Single(plan.Servers).Environments).ComposeProject);
    }

    [Fact]
    public void ANameThatALongerSlugOfTheOrganizationOwns_IsLeftToThatProject()
    {
        var plan = Plan(Inputs(
            [Library("portfolio.prod", new() { ["COMPOSE_PROJECT"] = "portfolio-api-prod", ["STATE_DIR"] = "/var/lib/portfolio-api" }),
             Library("portfolio.qa", new() { ["COMPOSE_PROJECT"] = "portfolio-qa" })],
            others: [new CleanupForeignProject("portfolio-api", true, []), new CleanupForeignProject("portfolio-qa", false, [])]));

        Assert.Equal(
            [("COMPOSE_PROJECT", "portfolio-api-prod"), ("STATE_DIR", "/var/lib/portfolio-api")],
            plan.Refused.Select(item => (item.Key, item.Value)));
        Assert.All(plan.Refused, item => Assert.Equal(ProjectDeletionRefusal.SharedWithAnotherProject, item.Reason));
        // A project of another organization never shares this one's servers: its slug claims nothing.
        Assert.Equal("portfolio-qa", Assert.Single(Assert.Single(plan.Servers).Environments).ComposeProject);
    }

    [Fact]
    public void AServerWhoseAgentCannotManageApacheOrCertbot_SaysWhatItLacks_OnlyWhenTheProjectHasHosts()
    {
        var full = "{\"software\":[\"project.resources\",\"apache.manage\",\"certbot.manage\"]}";
        var plan = Plan(Inputs(
            [Library("portfolio.share", Share),
             Library("portfolio.prod", new() { ["DOMAIN"] = "portfolio.example", ["COMPOSE_PROJECT"] = "portfolio-prod" }),
             Library("portfolio.qa", new() { ["COMPOSE_PROJECT"] = "portfolio-qa" }),
             Library("portfolio.staging", new() { ["DOMAIN"] = "portfolio.example", ["HOST_PREFIX"] = "staging-" })],
            [new CleanupEnvironment(21, "prod", [Prod]), new CleanupEnvironment(22, "qa", [Qa]),
             new CleanupEnvironment(23, "staging", [Prod with { Id = 6, CapabilitiesJson = full }])]));

        var prod = Assert.Single(plan.Servers, server => server.ServerId == 3);
        Assert.Equal(["apache.manage", "certbot.manage"], prod.MissingCapabilities);
        Assert.Equal(["apache.manage", "certbot.manage"], prod.ToDto().MissingCapabilities);
        Assert.False(prod.CanRemove(ProjectServerResourceKind.VirtualHost));
        Assert.False(prod.CanRemove(ProjectServerResourceKind.Certificate));
        Assert.True(prod.CanRemove(ProjectServerResourceKind.Container));
        Assert.Empty(Assert.Single(plan.Servers, server => server.ServerId == 4).MissingCapabilities);
        Assert.Empty(Assert.Single(plan.Servers, server => server.ServerId == 6).MissingCapabilities);
    }

    [Fact]
    public void TheSpecSentToTheAgent_CarriesTheProjectSlug_AndPassesItsValidation()
    {
        var plan = Plan(Inputs(
            [Library("portfolio.prod", new() { ["COMPOSE_PROJECT"] = "portfolio-prod", ["STATE_DIR"] = "/var/lib/portfolio-production" })]));

        var spec = Assert.Single(plan.Servers).Spec();
        Assert.Equal("portfolio", spec.Slug);
        Assert.Null(spec.Validate());
    }

    [Fact]
    public void AnEnvironmentWithoutServer_IsListedAsWhatStaysBehind()
    {
        var plan = Plan(Inputs(
            [Library("portfolio.staging", new() { ["COMPOSE_PROJECT"] = "portfolio-staging" })]));

        var refused = Assert.Single(plan.Refused);
        Assert.Equal(("staging", "portfolio-staging", ProjectDeletionRefusal.NoServer), (refused.Environment, refused.Value, refused.Reason));
        Assert.Empty(plan.Servers);
    }

    [Fact]
    public void AServerOfAnotherOrganization_OrDeleted_IsNeverAsked()
    {
        var deleted = Prod with { Id = 5, Deleted = true };
        var plan = Plan(Inputs(
            [Library("portfolio.prod", new() { ["COMPOSE_PROJECT"] = "portfolio-prod" })],
            [new CleanupEnvironment(21, "prod", [Foreign, deleted])],
            [new CleanupBackupPolicy(31, Foreign)]));

        Assert.Empty(plan.Servers);
        Assert.Equal(ProjectDeletionRefusal.NoServer, Assert.Single(plan.Refused).Reason);
    }

    [Fact]
    public void BackupPolicies_AddTheirArchives_ToTheServerThatKeepsThem()
    {
        var plan = Plan(Inputs([], policies: [new CleanupBackupPolicy(31, Prod), new CleanupBackupPolicy(32, Prod)]));

        var server = Assert.Single(plan.Servers);
        Assert.Equal([31, 32], server.BackupPolicyIds);
        Assert.Equal(["/var/lib/aetheus-agent/backups/31", "/var/lib/aetheus-agent/backups/32"],
            server.Spec().BackupPolicyIds.Select(ProjectServerResourceNames.BackupArchiveDirectory));
    }

    [Fact]
    public void AServerOffline_OrWithAnAgentTooOld_IsSaidSo()
    {
        var plan = Plan(Inputs(
            [Library("portfolio.prod", new() { ["COMPOSE_PROJECT"] = "portfolio-prod" }), Library("portfolio.qa", new() { ["COMPOSE_PROJECT"] = "portfolio-qa" })],
            [new CleanupEnvironment(21, "prod", [Prod with { Status = ServerStatus.Offline }]), new CleanupEnvironment(22, "qa", [Qa with { CapabilitiesJson = "{}" }])]));

        Assert.Equal(
            [ProjectDeletionServerState.Offline, ProjectDeletionServerState.AgentTooOld],
            plan.Servers.Select(server => server.State));
    }

    [Fact]
    public void AProjectCalledAetheus_NeverTargetsTheControlPlane_ItsValuesAreRefusedAndListed()
    {
        var plan = Plan(new ProjectServerCleanupInputs(7, "Aetheus", 1,
            [Library("aetheus.prod", new()
            {
                ["COMPOSE_PROJECT"] = "aetheus-prod",
                ["STATE_DIR"] = "/var/lib/aetheus-prod",
                ["APP_HOST"] = "aetheus.sonytumen.com",
                ["API_HOST"] = "aetheus-api.sonytumen.com",
            })],
            [new CleanupEnvironment(21, "prod", [Prod])], [], [], []));

        Assert.Empty(plan.Servers);
        Assert.Equal(
            ["COMPOSE_PROJECT=aetheus-prod", "STATE_DIR=/var/lib/aetheus-prod", "APP_HOST=aetheus.sonytumen.com", "API_HOST=aetheus-api.sonytumen.com"],
            plan.Refused.Select(item => $"{item.Key}={item.Value}"));
        Assert.All(plan.Refused, item => Assert.Equal(ProjectDeletionRefusal.ReservedForControlPlane, item.Reason));
    }

    private static readonly CleanupServerInfo VpsSim = new(11, "vpssim", 1, ServerStatus.Online, Capable, false);
    private static readonly CleanupServerInfo Runner = new(12, "runner", 1, ServerStatus.Online, Capable, false);

    /// <summary>A project the pipeline setup wizard created (W8): libraries w8.share, w8.prod and w8.qa, no Aetheus
    /// environment, the production server attached to the project and named by the deployment's deployServer.</summary>
    private static ProjectServerCleanupInputs Wizard(IReadOnlyList<CleanupServerInfo> linked, IReadOnlyList<string>? deployServers = null) =>
        new(27, "W8", 1,
        [
            Library("w8.share", new() { ["APP_SUBDOMAIN"] = "app", ["API_SUBDOMAIN"] = "api" }),
            Library("w8.prod", new() { ["DOMAIN"] = "w8.test", ["COMPOSE_PROJECT"] = "w8-prod", ["STATE_DIR"] = "/var/lib/w8-production" }),
            Library("w8.qa", new() { ["COMPOSE_PROJECT"] = "w8-qa" }),
        ],
        [],
        [.. linked.Select((server, index) => new CleanupProjectServer(40 + index, server.Name, server))],
        [], [], deployServers);

    [Fact]
    public void AWizardProject_WithoutEnvironment_IsCleanedOnTheServerItsDeploymentTargets()
    {
        var plan = Plan(Wizard([VpsSim], ["vpssim"]));

        var target = Assert.Single(plan.Servers);
        Assert.Equal((VpsSim.Id, ProjectDeletionServerState.Ready), (target.ServerId, target.State));
        var prod = Assert.Single(target.Environments);
        Assert.Equal("prod", prod.Name);
        Assert.Equal("w8-prod", prod.ComposeProject);
        Assert.Equal(["app.w8.test", "api.w8.test"], prod.Hosts);
        Assert.Equal("/var/lib/w8-production", prod.StateDirectory);
        // The QA stack is a disposable one on the CI runner, torn down by its own run: nothing locates it on a server.
        var qa = Assert.Single(plan.Refused);
        Assert.Equal(("qa", "w8-qa", ProjectDeletionRefusal.NoServer), (qa.Environment, qa.Value, qa.Reason));
    }

    [Fact]
    public void TheDeploymentsServer_WinsOverTheOtherServersLinkedToTheProject()
    {
        var plan = Plan(Wizard([Runner, VpsSim], ["vpssim"]));

        Assert.Equal([VpsSim.Id], plan.Servers.Select(server => server.ServerId));
    }

    [Fact]
    public void WithoutADeploymentServer_TheServersLinkedToTheProject_AreAsked()
    {
        var plan = Plan(Wizard([VpsSim]));

        Assert.Equal("w8-prod", Assert.Single(Assert.Single(plan.Servers).Environments).ComposeProject);
    }

    [Fact]
    public void ADeploymentServerTheProjectDoesNotHave_IsNeverInvented()
    {
        var plan = Plan(Wizard([], ["vpssim"]));

        Assert.Empty(plan.Servers);
        Assert.All(plan.Refused, item => Assert.Equal(ProjectDeletionRefusal.NoServer, item.Reason));
        Assert.Equal(["w8-prod", "app.w8.test", "api.w8.test", "/var/lib/w8-production", "w8-qa"], plan.Refused.Select(item => item.Value));
    }

    [Fact]
    public void AnEnvironmentWithoutServer_TakesTheServerItsDeploymentTargets()
    {
        var inputs = Wizard([VpsSim], ["vpssim"]) with { Environments = [new CleanupEnvironment(21, "production", [])] };

        var target = Assert.Single(Plan(inputs).Servers);
        Assert.Equal((VpsSim.Id, "production"), (target.ServerId, Assert.Single(target.Environments).Name));
    }

    [Fact]
    public void ALinkedServerOfAnotherOrganization_OrDeleted_IsNeverAsked()
    {
        var foreign = VpsSim with { OrganizationId = 2 };
        var deleted = Runner with { Deleted = true };

        var plan = Plan(Wizard([foreign, deleted], ["vpssim"]));

        Assert.Empty(plan.Servers);
        Assert.Contains(plan.Refused, item => item is { Value: "w8-prod", Reason: ProjectDeletionRefusal.NoServer });
    }

    [Fact]
    public void ALinkedServerTheUserMayNotWriteTo_IsListedAsSuch_NeverAsked()
    {
        var plan = Plan(Wizard([VpsSim], ["vpssim"]), new ProjectServerCleanupGuards(ServerWriteAccess.None, new HashSet<string>()));

        Assert.Equal(ProjectDeletionServerState.NoWriteAccess, Assert.Single(plan.Servers).State);
    }

    [Theory]
    [InlineData("$(A)-$(B:-)", "x-")]
    [InlineData("$(C)", "x")]
    [InlineData("$(LOOP)", null)]
    [InlineData("$(A", null)]
    [InlineData("${A}", null)]
    public void Expand_ResolvesReferences_AndRefusesWhatItCannot(string value, string? expected)
    {
        var entries = new Dictionary<string, string> { ["A"] = "x", ["C"] = "$(A)", ["LOOP"] = "$(LOOP)" };

        Assert.Equal(expected, ProjectServerCleanupLibraryValues.Expand(value, entries));
    }

    [Fact]
    public void TheBlueGreenUpstreamFile_AndTheImagesOfTheComposeProject_AreInThePlan_FromTheLibraryValues()
    {
        // The library the setup wizard writes for a blue-green production (live run on W9, recette R-97).
        var plan = Plan(Inputs([Library("portfolio.prod", new()
        {
            ["COMPOSE_PROJECT"] = "portfolio-prod",
            ["STATE_DIR"] = "/var/lib/portfolio-production",
            ["UPSTREAM_DEFINE"] = "PORTFOLIO",
            ["UPSTREAM_CONF"] = "/etc/apache2/sites-available/000-$(COMPOSE_PROJECT)-upstream.conf",
        })]));

        Assert.Empty(plan.Refused);
        var server = Assert.Single(plan.Servers);
        var environment = Assert.Single(server.Environments);
        Assert.Equal("/etc/apache2/sites-available/000-portfolio-prod-upstream.conf", environment.UpstreamConfiguration);
        var spec = server.Spec();
        Assert.Equal(["/etc/apache2/sites-available/000-portfolio-prod-upstream.conf"], spec.UpstreamConfigurations);
        Assert.Null(spec.Validate());
        Assert.True(spec.Covers(new(ProjectServerResourceKind.UpstreamConfiguration, environment.UpstreamConfiguration!, environment.UpstreamConfiguration!)));
        Assert.True(spec.Covers(new(ProjectServerResourceKind.Image, "portfolio-prod-back-blue:latest", "portfolio-prod")));
        Assert.Equal("prod", server.EnvironmentOf(ProjectServerResourceKind.Image, "portfolio-prod"));
        Assert.Equal("prod", server.EnvironmentOf(ProjectServerResourceKind.UpstreamConfiguration, environment.UpstreamConfiguration!));
        // The upstream file is removed through the Apache site operations: an agent without apache.manage keeps it.
        Assert.Equal([AgentCapabilities.ApacheManagement], server.MissingCapabilities);
        Assert.False(server.CanRemove(ProjectServerResourceKind.UpstreamConfiguration));
        Assert.True(server.CanRemove(ProjectServerResourceKind.Image));
    }

    [Theory]
    [InlineData("/etc/apache2/sites-available/000-default.conf", ProjectDeletionRefusal.NotNamedAfterProject)]
    [InlineData("/etc/apache2/sites-available/000-shop-upstream.conf", ProjectDeletionRefusal.NotNamedAfterProject)]
    [InlineData("/etc/apache2/sites-available/000-aetheus-upstream.conf", ProjectDeletionRefusal.ReservedForControlPlane)]
    [InlineData("/etc/apache2/sites-available/../apache2.conf", ProjectDeletionRefusal.InvalidName)]
    [InlineData("/etc/apache2/sites-available/sub/000-portfolio-upstream.conf", ProjectDeletionRefusal.InvalidName)]
    [InlineData("/etc/apache2/sites-enabled/000-portfolio-upstream.conf", ProjectDeletionRefusal.InvalidName)]
    [InlineData("/etc/apache2/conf-available/portfolio-upstream.conf", ProjectDeletionRefusal.InvalidName)]
    [InlineData("/etc/apache2/sites-available/000-portfolio-upstream", ProjectDeletionRefusal.InvalidName)]
    [InlineData("/etc/apache2/sites-available/portfolio..conf", ProjectDeletionRefusal.InvalidName)]
    public void AnUpstreamFile_OutsideSitesAvailable_OrNotNamedAfterTheProject_IsRefused(string value, ProjectDeletionRefusal reason)
    {
        var plan = Plan(Inputs([Library("portfolio.prod", new() { ["COMPOSE_PROJECT"] = "portfolio-prod", ["UPSTREAM_CONF"] = value })]));

        var refused = Assert.Single(plan.Refused);
        Assert.Equal(("UPSTREAM_CONF", value, reason), (refused.Key, refused.Value, refused.Reason));
        Assert.Null(Assert.Single(Assert.Single(plan.Servers).Environments).UpstreamConfiguration);
    }

    [Fact]
    public void AnUpstreamFileAnotherProjectNames_IsRefused()
    {
        const string Shared = "/etc/apache2/sites-available/000-portfolio-upstream.conf";
        var other = new CleanupForeignProject("blog", true, [Library("blog.prod", new() { ["UPSTREAM_CONF"] = Shared })]);

        var plan = Plan(Inputs([Library("portfolio.prod", new() { ["COMPOSE_PROJECT"] = "portfolio-prod", ["UPSTREAM_CONF"] = Shared })], others: [other]));

        Assert.Equal(ProjectDeletionRefusal.SharedWithAnotherProject, Assert.Single(plan.Refused).Reason);
    }
}
