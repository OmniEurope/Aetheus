// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Tests.Projects;

/// <summary> Recette R-97 (ADR-058): the strict names, the specification the agent receives and the lines it
/// prints back. These run on both sides (backend and agent), so an injection refused here is refused twice. </summary>
public sealed class ProjectServerResourceSpecTests
{
    private static readonly ProjectServerResourceSpec Identity = new()
    {
        Slug = "portfolio",
        ComposeProjects = ["portfolio-prod"],
        Hosts = ["app.portfolio.example"],
        StateDirectories = ["/var/lib/portfolio-production"],
        BackupPolicyIds = [31],
    };

    [Theory]
    [InlineData("portfolio-prod", true)]
    [InlineData("w6-prod;rm -rf /", false)]
    [InlineData("w6-prod\n", false)]
    [InlineData("w6 prod", false)]
    [InlineData("-prod", false)]
    [InlineData("$(id)", false)]
    [InlineData("", false)]
    public void ComposeProject_AdmitsOnlyAStrictName(string value, bool expected) =>
        Assert.Equal(expected, ProjectServerResourceNames.IsComposeProject(value));

    [Theory]
    [InlineData("app.portfolio.example", true)]
    [InlineData("app.portfolio.example\n", false)]
    [InlineData("*.portfolio.example", false)]
    [InlineData("app portfolio.example", false)]
    [InlineData("app/portfolio.example", false)]
    [InlineData("../portfolio.example", false)]
    [InlineData("App.Portfolio.Example", false)]
    [InlineData("portfolio.example.", false)]
    public void Host_AdmitsOnlyALowerCaseDnsName(string value, bool expected) =>
        Assert.Equal(expected, ProjectServerResourceNames.IsHost(value));

    [Theory]
    [InlineData("/var/lib/portfolio-production", true)]
    [InlineData("/", false)]
    [InlineData("", false)]
    [InlineData("portfolio", false)]
    [InlineData("/var/lib", false)]
    [InlineData("/var/lib/", false)]
    [InlineData("/var/lib/..", false)]
    [InlineData("/var/lib/portfolio..x", false)]
    [InlineData("/var/lib/portfolio/../../etc", false)]
    [InlineData("/var/lib/portfolio/data", false)]
    [InlineData("/var/lib/docker", false)]
    [InlineData("/var/lib/postgresql", false)]
    [InlineData("/var/lib/aetheus-agent", false)]
    [InlineData("/var/lib/aetheus-agent-x", false)]
    [InlineData("/etc/portfolio", false)]
    [InlineData("/var/lib/portfolio\n", false)]
    public void StateDirectory_IsConfinedToADirectChildOfVarLib_NeverASystemOne(string value, bool expected) =>
        Assert.Equal(expected, ProjectServerResourceNames.IsStateDirectory(value));

    [Theory]
    [InlineData("/var/lib/portfolio", true)]
    [InlineData("/var/lib/portfolio-production", true)]
    [InlineData("/var/lib/portfolios", false)]
    [InlineData("/var/lib/other-production", false)]
    public void StateDirectoryOf_BelongsToTheProjectSlug(string value, bool expected) =>
        Assert.Equal(expected, ProjectServerResourceNames.IsStateDirectoryOf(value, "portfolio"));

    [Theory]
    [InlineData("/var/lib/aetheus-agent/projects/w9-production", true)]
    [InlineData("/var/lib/aetheus-agent/projects/w9", true)]
    [InlineData("/var/lib/w9-production", true)]
    [InlineData("/var/lib/aetheus-agent/projects/other-production", false)]
    [InlineData("/var/lib/aetheus-agent/projects/w90-production", false)]
    [InlineData("/var/lib/aetheus-agent/w", false)]
    [InlineData("/var/lib/aetheus-agent/keys", false)]
    [InlineData("/var/lib/aetheus-agent/deploy", false)]
    [InlineData("/var/lib/aetheus-agent/backups", false)]
    [InlineData("/var/lib/aetheus-agent/projects", false)]
    [InlineData("/var/lib/aetheus-agent/projects/", false)]
    [InlineData("/var/lib/aetheus-agent/projects/../keys", false)]
    [InlineData("/var/lib/aetheus-agent/projects/..", false)]
    [InlineData("/var/lib/aetheus-agent/projects/w9..production", false)]
    [InlineData("/var/lib/aetheus-agent/projects/w9-production.", false)]
    [InlineData("/var/lib/aetheus-agent/projects/w9-production/sub", false)]
    [InlineData("/var/lib/aetheus-agent/projects/w9-production/", false)]
    [InlineData("/var/lib/aetheus-agent/projects/w9-production\n", false)]
    [InlineData("/var/lib/aetheus-agent/projects/W9-production", false)]
    [InlineData("/var/lib/aetheus-agent/projects//w9-production", false)]
    [InlineData("/var/lib/aetheus-agent/other/w9-production", false)]
    [InlineData("/var/lib/aetheus-agent-x/projects/w9-production", false)]
    public void StateDirectoryOf_AcceptsTheProjectsDirectoryTheAgentOwns_AndNothingElseUnderIt(string value, bool expected) =>
        Assert.Equal(expected, ProjectServerResourceNames.IsStateDirectoryOf(value, "w9"));

    [Fact]
    public void StateDirectory_InsideTheProjectsDirectory_StaysOutOfTheControlPlaneNamespace() =>
        Assert.False(ProjectServerResourceNames.IsStateDirectory("/var/lib/aetheus-agent/projects/aetheus-production"));

    [Fact]
    public void TheWizardsStateDirectory_IsOneTheProjectDeletionMayRemove()
    {
        var path = ProjectServerResourceNames.ProjectStateDirectory("w9", "production");

        Assert.Equal("/var/lib/aetheus-agent/projects/w9-production", path);
        Assert.True(ProjectServerResourceNames.IsStateDirectoryOf(path, "w9"));
        Assert.Null((Identity with { Slug = "w9", ComposeProjects = [], StateDirectories = [path] }).Validate());
    }

    [Theory]
    [InlineData("/var/lib/mysql", "mysql")]
    [InlineData("/var/lib/redis", "redis")]
    [InlineData("/var/lib/letsencrypt", "letsencrypt")]
    [InlineData("/var/lib/grafana", "grafana")]
    [InlineData("/var/lib/fail2ban", "fail2ban")]
    [InlineData("/var/lib/containerd", "containerd")]
    [InlineData("/var/lib/aetheus", "aetheus")]
    [InlineData("/var/lib/teamspeak", "teamspeak")]
    public void AServiceDirectory_IsNeverAProjectStateDirectory_EvenForAProjectNamedLikeIt(string value, string slug) =>
        Assert.False(ProjectServerResourceNames.IsStateDirectoryOf(value, slug));

    [Theory]
    [InlineData("/var/lib/aetheus-agent/.deployments/portfolio-prod", "portfolio-prod", true)]
    [InlineData("/var/lib/aetheus-agent/.deployments/portfolio-prod", "portfolio-qa", false)]
    [InlineData("/var/lib/aetheus-agent/.deployments/portfolio-prod/", "portfolio-prod", false)]
    [InlineData("/var/lib/aetheus-agent/.deployments/portfolio-prod/sub", "portfolio-prod", false)]
    [InlineData("/var/lib/aetheus-agent/.deployments/../keys", "../keys", false)]
    [InlineData("/var/lib/aetheus-agent/.deployments/..", "..", false)]
    [InlineData("/var/lib/aetheus-agent/.deployments/a/b", "a/b", false)]
    [InlineData("/var/lib/aetheus-agent/.deployments/portfolio-prod\n", "portfolio-prod\n", false)]
    [InlineData("/var/lib/aetheus-agent/.deployments/aetheus-prod", "aetheus-prod", false)]
    [InlineData("/var/lib/aetheus-agent/.deployments", "", false)]
    [InlineData("/home/other/.deployments/portfolio-prod", "portfolio-prod", false)]
    public void DeploymentDirectory_IsExactlyTheTemplatesDeployDirOfAComposeProject(string path, string project, bool expected) =>
        Assert.Equal(expected, ProjectServerResourceNames.IsResource(ProjectServerResourceKind.DeploymentDirectory, path, project));

    [Fact]
    public void DeploymentDirectory_IsWhatTheDeployTemplatesWrite_ForTheComposeProject()
    {
        Assert.Equal("/var/lib/aetheus-agent/.deployments/portfolio-prod", ProjectServerResourceNames.DeploymentDirectory("portfolio-prod"));
        Assert.True(ProjectServerResourceNames.IsInDeploymentsBase("/var/lib/aetheus-agent/.deployments/portfolio-prod"));
        Assert.False(ProjectServerResourceNames.IsInDeploymentsBase("/var/lib/aetheus-agent/.deployments/../keys"));
    }

    [Fact]
    public void Validate_TiesTheStateDirectoriesAndComposeProjects_ToTheSlug()
    {
        Assert.NotNull((Identity with { Slug = "" }).Validate());
        Assert.NotNull((Identity with { Slug = "../etc" }).Validate());
        Assert.NotNull((Identity with { StateDirectories = ["/var/lib/other-production"] }).Validate());
        Assert.NotNull((Identity with { ComposeProjects = ["other-prod"] }).Validate());
        Assert.Null((Identity with { ComposeProjects = ["portfolio_prod"], StateDirectories = ["/var/lib/portfolio"] }).Validate());
    }

    [Theory]
    [InlineData("aetheus-prod")]
    [InlineData("aetheus")]
    [InlineData("aetheus_prod")]
    public void Validate_RefusesAComposeProjectOfTheControlPlaneNamespace_EvenForAProjectCalledAetheus(string compose) =>
        Assert.NotNull((Identity with { Slug = "aetheus", ComposeProjects = [compose], Hosts = [], StateDirectories = [] }).Validate());

    [Theory]
    [InlineData("/var/lib/aetheus-prod")]
    [InlineData("/var/lib/aetheus-production")]
    [InlineData("/var/lib/aetheus_data")]
    public void Validate_RefusesAStateDirectoryOfTheControlPlaneNamespace_EvenForAProjectCalledAetheus(string directory) =>
        Assert.NotNull((Identity with { Slug = "aetheus", ComposeProjects = [], Hosts = [], StateDirectories = [directory] }).Validate());

    [Theory]
    [InlineData("aetheus.sonytumen.com")]
    [InlineData("aetheus-api.sonytumen.com")]
    [InlineData("aetheus-accept.example.com")]
    public void Validate_RefusesAHostOfTheControlPlaneNamespace(string host) =>
        Assert.NotNull((Identity with { Hosts = [host] }).Validate());

    [Fact]
    public void Covers_RefusesADockerObjectOfTheControlPlaneNamespace_InsideTheProjectsComposeProject()
    {
        Assert.False(Identity.Covers(new(ProjectServerResourceKind.Container, "aetheus-prod-back-1", "portfolio-prod")));
        Assert.False(Identity.Covers(new(ProjectServerResourceKind.Volume, "aetheus_data", "portfolio-prod")));
        Assert.False(Identity.Covers(new(ProjectServerResourceKind.Network, "aetheus-prod_default", "portfolio-prod")));
        Assert.False(Identity.Covers(new(ProjectServerResourceKind.VirtualHost, "aetheus.example.com.conf", "aetheus.example.com")));
        Assert.False(Identity.Covers(new(ProjectServerResourceKind.Certificate, "aetheus-api.example.com", "aetheus-api.example.com")));
    }

    [Fact]
    public void TheControlPlaneNamespace_IsTheWordAetheus_NotEveryNameThatStartsWithIt()
    {
        Assert.Null((Identity with { Slug = "aetheusx", ComposeProjects = ["aetheusx-prod"], Hosts = ["aetheusx.example.com"], StateDirectories = ["/var/lib/aetheusx-prod"] }).Validate());
        Assert.True(Identity.Covers(new(ProjectServerResourceKind.Container, "portfolio-prod-aetheus-1", "portfolio-prod")));
    }

    [Fact]
    public void Covers_AdmitsOnlyResourcesOfTheIdentity()
    {
        Assert.True(Identity.Covers(new(ProjectServerResourceKind.Volume, "portfolio-prod_db", "portfolio-prod")));
        Assert.True(Identity.Covers(new(ProjectServerResourceKind.VirtualHost, "app.portfolio.example-ssl.conf", "app.portfolio.example")));
        Assert.True(Identity.Covers(new(ProjectServerResourceKind.BackupArchives, "/var/lib/aetheus-agent/backups/31", "/var/lib/aetheus-agent/backups/31")));
        Assert.False(Identity.Covers(new(ProjectServerResourceKind.Volume, "other_db", "other-prod")));
        Assert.False(Identity.Covers(new(ProjectServerResourceKind.VirtualHost, "000-default.conf", "app.portfolio.example")));
        Assert.False(Identity.Covers(new(ProjectServerResourceKind.Certificate, "other.example", "app.portfolio.example")));
        Assert.False(Identity.Covers(new(ProjectServerResourceKind.StateDirectory, "/var/lib/docker", "/var/lib/docker")));
        Assert.False(Identity.Covers(new(ProjectServerResourceKind.BackupArchives, "/var/lib/aetheus-agent/backups/32", "/var/lib/aetheus-agent/backups/32")));
        Assert.False(Identity.Covers(new(ProjectServerResourceKind.Container, "web;reboot", "portfolio-prod")));
    }

    [Fact]
    public void Validate_RefusesAValueOrARemovalOutsideTheIdentity()
    {
        Assert.Null(Identity.Validate());
        Assert.NotNull((Identity with { ComposeProjects = ["x;rm -rf /"] }).Validate());
        Assert.NotNull((Identity with { StateDirectories = ["/"] }).Validate());
        Assert.NotNull((Identity with { BackupPolicyIds = [0] }).Validate());
        Assert.NotNull((Identity with { Remove = [new(ProjectServerResourceKind.Volume, "other_db", "other-prod")] }).Validate());
        Assert.NotNull((Identity with { Hosts = [.. Enumerable.Range(0, 65).Select(index => $"h{index}.portfolio.example")] }).Validate());
    }

    [Fact]
    public void Validate_RefusesANullElement_InEveryList()
    {
        Assert.NotNull((Identity with { ComposeProjects = ["portfolio-prod", null!] }).Validate());
        Assert.NotNull((Identity with { Hosts = [null!] }).Validate());
        Assert.NotNull((Identity with { StateDirectories = [null!] }).Validate());
        Assert.NotNull((Identity with { Remove = [null!] }).Validate());
        // The same through the wire: a JSON null in a list must not slip past the agent.
        var json = "{\"slug\":\"portfolio\",\"composeProjects\":[null],\"hosts\":[],\"stateDirectories\":[],\"backupPolicyIds\":[],\"remove\":[null]}";
        Assert.Null(ProjectServerResourceSpec.Decode(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)), out var refusal));
        Assert.NotNull(refusal);
    }

    [Fact]
    public void Decode_ReadsBackWhatEncodeWrote_AndRefusesAnythingElse()
    {
        var removal = Identity with { Remove = [new(ProjectServerResourceKind.Container, "portfolio-prod-web-1", "portfolio-prod")] };

        var decoded = ProjectServerResourceSpec.Decode(removal.Encode(), out var refusal);

        Assert.Null(refusal);
        Assert.NotNull(decoded);
        Assert.Equal(removal.Remove, decoded.Remove);
        Assert.Equal(removal.Hosts, decoded.Hosts);
        Assert.Null(ProjectServerResourceSpec.Decode("not base64!", out refusal));
        Assert.NotNull(refusal);
        Assert.Null(ProjectServerResourceSpec.Decode((Identity with { StateDirectories = ["/etc"] }).Encode(), out refusal));
        Assert.Contains("/etc", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ReadsTheAgentLines_AndTurnsARefusedNameIntoAnUnreadableEntry()
    {
        var volume = new ProjectServerResourceRef(ProjectServerResourceKind.Volume, "portfolio-prod_db", "portfolio-prod");
        var output = string.Join('\n',
            ProjectServerResourceOutput.Line(ProjectServerResourceOutcome.Failed, volume, "volume is in use\tby web"),
            ProjectServerResourceOutput.Unreadable(ProjectServerResourceKind.Certificate, "app.portfolio.example", "permission denied"),
            "REMOVED\tContainer\tweb;reboot\tportfolio-prod",
            "REMOVED\t3\tx\tportfolio-prod",
            "noise from the agent",
            ProjectServerResourceOutput.CompleteLine);

        var report = ProjectServerResourceOutput.Parse(output);

        Assert.True(report.Complete);
        var line = Assert.Single(report.Resources);
        Assert.Equal((ProjectServerResourceOutcome.Failed, "portfolio-prod_db", "volume is in use by web"), (line.Outcome, line.Name, line.Reason));
        Assert.Equal(3, report.Unreadable.Count);
        Assert.Contains(report.Unreadable, item => item.Reason.Contains("web;reboot", StringComparison.Ordinal));
        Assert.False(ProjectServerResourceOutput.Parse(string.Join('\n', ProjectServerResourceOutput.Line(ProjectServerResourceOutcome.Found, volume))).Complete);
    }
}
