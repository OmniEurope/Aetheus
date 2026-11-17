// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Projects.ProjectDetailSections;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages;

public class ProjectOverviewSectionTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public ProjectOverviewSectionTests() => _handler = BunitTestHelper.RegisterServices(this);

    [Fact]
    public void Renders_NullProject_ShowsZoneVeil()
    {
        var cut = Render<ProjectOverviewSection>(p => p.Add(x => x.Project, (ProjectDetailDto?)null));
        // Null project → only the branded loading indicator renders.
        Assert.Contains("aetheus-loader-logo", cut.Markup);
    }

    [Fact]
    public void Renders_WithRecentPipelinesAndCommits()
    {
        _handler.SetJsonResponse("api/pipelines/runs/recent?projectId=1", new List<PipelineRunDto>
        {
            new()
            {
                Id = 42,
                BuildNumber = 42,
                PipelineId = 7,
                PipelineName = "Release",
                ProjectId = 1,
                Status = PipelineStatus.Success,
                StartedAt = new DateTime(2026, 1, 2, 10, 0, 0),
                Steps = [new PipelineStepRunDto { Id = 1, TriggeredRunId = 43 }]
            },
            new()
            {
                Id = 43,
                BuildNumber = 43,
                PipelineId = 8,
                PipelineName = "Deploy",
                ProjectId = 1,
                Status = PipelineStatus.Success,
                StartedAt = new DateTime(2026, 1, 2, 10, 1, 0)
            }
        });
        _handler.SetPaginatedJsonResponse(HttpMethod.Get,
            "api/git/repos?page=1&pageSize=100&projectId=1&sortDescending=False",
            new List<GitLightRepoDto>
        {
            new() { Id = 3, ProjectId = 1, Name = "web", DefaultBranch = "main" }
        });
        _handler.SetJsonResponse("api/git/repos/3/commits", new PaginatedResult<GitLightCommitDto>
        {
            Items =
            [
                new()
                {
                    Sha = "abcdef123456",
                    ShortSha = "abcdef1",
                    Message = "Improve overview",
                    AuthorName = "Alice",
                    AuthorDate = new DateTime(2026, 1, 2, 9, 0, 0),
                    RefNames = ["develop"]
                }
            ],
            TotalCount = 1
        });
        _handler.SetJsonResponse("api/releases", new PaginatedResult<ReleaseDto>());
        _handler.SetPaginatedJsonResponse("api/git/repos/3/tree", new List<GitLightTreeEntryDto>());

        var cut = Render<ProjectOverviewSection>(p =>
            p.Add(x => x.Project, new ProjectDetailDto
            {
                Id = 1,
                Name = "Web",
                Description = "A project",
                RepositoryUrl = "https://example/repo",
                DefaultBranch = "main",
                LatestGateGrade = AnalysisGrade.C
            }));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("RecentPipelines", cut.Markup);
            Assert.Contains("Release", cut.Markup);
            Assert.Contains(">#42<", cut.Markup);
            Assert.DoesNotContain("Deploy", cut.Markup);
            Assert.Contains("RecentCommits", cut.Markup);
            Assert.True(cut.Markup.IndexOf("RecentCommits", StringComparison.Ordinal) <
                        cut.Markup.IndexOf("RecentPipelines", StringComparison.Ordinal));
            Assert.Contains("Improve overview", cut.Markup);
            Assert.Contains("abcdef1", cut.Markup);
            Assert.Contains("Branch", cut.Markup);
            Assert.Contains("main", cut.Markup);
            Assert.Contains("develop", cut.Markup);
            // Use the shared filled grade badge (R-107); C has the warning tone.
            var grade = Assert.Single(cut.FindAll(".grade-badge"));
            Assert.Equal("C", grade.TextContent.Trim());
            Assert.Contains("grade-badge--c", grade.ClassName, StringComparison.Ordinal);
            Assert.Contains("omni-badge--solid", grade.ClassName, StringComparison.Ordinal);
            Assert.Contains("href=\"/projects/1/quality\"", cut.Markup);
            Assert.DoesNotContain("RecentActivity", cut.Markup);
            Assert.Equal(5, cut.FindComponent<PipelineRunsGrid>().Instance.MaxGroups);
            Assert.Contains(_handler.Requests, request =>
                request.Method == "GET"
                && request.Url.Contains("api/git/repos/3/commits", StringComparison.Ordinal)
                && (request.Url.Contains("ref=%2A", StringComparison.OrdinalIgnoreCase)
                    || request.Url.Contains("ref=*", StringComparison.Ordinal)));
        });

        cut.Find("button.omni-data-grid__expand").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Deploy", cut.Markup);
            // R-8: the hierarchy tree writes the number like the grid does.
            Assert.Equal("#43", cut.Find("a.pipeline-run-tree-id").TextContent.Trim());
        });
    }

    /// <summary>Recette R-69 (OE2): the "Create the Git repository" step showed "done" beside "no recent commit" for a project
    /// whose source is an external repository (or should be) and holds no readable commit. It stays to do and leads to the
    /// external repository; a project whose external mirror holds code is done.</summary>
    [Theory]
    [InlineData(true, false, false)] // the mirror is the project's source and was never synchronized
    [InlineData(false, true, false)] // the mirror holds code but sits beside an internal repository that holds none
    [InlineData(false, false, true)] // the mirror is the project's source and holds code
    public void TheGitStep_OfAnExternalSource_IsDoneOnlyWhenTheMirrorHoldsACommit(bool mirrorEmpty, bool besideEmptyInternal, bool expectedDone)
    {
        var repositories = new List<GitLightRepoDto>
        {
            new()
            {
                Id = 5, ProjectId = 17, Name = "OmniEurope.Blazor", Slug = "omnieurope-blazor", DefaultBranch = "main",
                IsEmpty = mirrorEmpty, IsExternalMirror = true, IsAdditionalSource = besideEmptyInternal
            }
        };
        if (besideEmptyInternal)
            repositories.Add(new() { Id = 4, ProjectId = 17, Name = "oe2", Slug = "oe2", DefaultBranch = "main", IsEmpty = true });
        _handler.SetJsonResponse("api/pipelines/runs/recent?projectId=17", new List<PipelineRunDto>());
        _handler.SetPaginatedJsonResponse(HttpMethod.Get,
            "api/git/repos?page=1&pageSize=100&projectId=17&sortDescending=False", repositories);
        _handler.SetJsonResponse("api/releases", new PaginatedResult<ReleaseDto>());

        var cut = Render<ProjectOverviewSection>(p =>
            p.Add(x => x.Project, new ProjectDetailDto { Id = 17, Name = "OE2", RepositoryUrl = "https://example/git/17/oe.git" }));

        cut.WaitForAssertion(() =>
        {
            var step = cut.FindAll("tr[data-testid='onboarding-step']").First();
            var done = step.QuerySelector(".omni-badge")!.TextContent.Contains("Done", StringComparison.Ordinal);
            Assert.Equal(expectedDone, done);
            if (expectedDone) return;
            Assert.Equal("/projects/17/external-repo", step.QuerySelector("a")!.GetAttribute("href"));
        });
    }

    [Fact]
    public void HasAnExpandContractTab_BetweenOverviewAndReadme_ThatReadsTheEnvironmentsOnlyWhenOpened()
    {
        // Recette R-10: the project's database tracking sits in its own tab, grouped by environment (production first).
        _handler.SetJsonResponse("api/pipelines/runs/recent?projectId=1", new List<PipelineRunDto>());
        _handler.SetPaginatedJsonResponse<GitLightRepoDto>(HttpMethod.Get,
            "api/git/repos?page=1&pageSize=100&projectId=1&sortDescending=False", []);
        _handler.SetJsonResponse("api/releases", new PaginatedResult<ReleaseDto>());
        _handler.SetPaginatedJsonResponse(HttpMethod.Get, "api/environments?",
            new[] { new EnvironmentDto { Id = 12, Name = "Prod", Type = EnvironmentType.Production, ProjectId = 1 } });
        _handler.SetJsonResponse("api/environments/12/schema-tracking", new EnvironmentSchemaDto { EnvironmentId = 12 });

        var cut = Render<ProjectOverviewSection>(p =>
            p.Add(x => x.Project, new ProjectDetailDto { Id = 1, Name = "Web", MigrationGateEnabled = true }));

        var tabs = cut.FindAll("[role='tab']").Select(tab => tab.TextContent.Trim()).ToList();
        Assert.Collection(tabs,
            tab => Assert.Contains("Overview", tab, StringComparison.Ordinal),
            tab => Assert.Contains("SchemaTrackingTabProject", tab, StringComparison.Ordinal),
            tab => Assert.Contains("Readme", tab, StringComparison.Ordinal),
            tab => Assert.Contains("Changelog", tab, StringComparison.Ordinal));
        Assert.DoesNotContain(_handler.Requests, r => r.Url.Contains("schema-tracking", StringComparison.Ordinal));

        cut.FindAll("[role='tab']")[1].Click();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='project-schema-environment']")));
        Assert.Contains(_handler.Requests, r => r.Url.EndsWith("api/environments/12/schema-tracking", StringComparison.Ordinal));
    }

    /// <summary>Recette R-13 (user decision 2026-11-09): the expand/contract tab needs an environment AND a database, the two
    /// together (so never a package), or the migration gate turned on; the project holds no other trace of a database than what
    /// its pipelines declare.</summary>
    [Theory]
    [InlineData(0, true, false, false)]
    [InlineData(2, false, false, false)]
    [InlineData(0, false, true, true)]
    [InlineData(2, true, false, true)]
    [InlineData(0, true, true, true)]
    [InlineData(2, false, true, true)]
    public void ExpandContractTab_IsShownOnlyWithAnEnvironmentAndADatabase_OrTheMigrationGateOn(
        int environmentCount, bool declaresDatabase, bool gateEnabled, bool shown)
    {
        _handler.SetJsonResponse("api/pipelines/runs/recent?projectId=1", new List<PipelineRunDto>());
        _handler.SetPaginatedJsonResponse<GitLightRepoDto>(HttpMethod.Get,
            "api/git/repos?page=1&pageSize=100&projectId=1&sortDescending=False", []);
        _handler.SetJsonResponse("api/releases", new PaginatedResult<ReleaseDto>());
        _handler.SetPaginatedJsonResponse(HttpMethod.Get, "api/environments?", Array.Empty<EnvironmentDto>());
        var yaml = declaresDatabase
            ? "name: Ci\nextends: 'application-ci@3'\nvariables:\n  APPLICATION_HAS_DATABASE: \"true\"\n"
            : "name: Ci\nextends: 'application-ci@3'\nvariables:\n  APPLICATION_HAS_DATABASE: \"false\"\n";

        var cut = Render<ProjectOverviewSection>(p => p.Add(x => x.Project, new ProjectDetailDto
        {
            Id = 1,
            Name = "Web",
            EnvironmentCount = environmentCount,
            MigrationGateEnabled = gateEnabled,
            Pipelines = [new PipelineDto { Id = 5, Name = "Ci", YamlDefinition = yaml }]
        }));

        var tabs = cut.FindAll("[role='tab']").Select(tab => tab.TextContent.Trim()).ToList();
        Assert.Equal(shown, tabs.Any(tab => tab.Contains("SchemaTrackingTabProject", StringComparison.Ordinal)));
        Assert.Equal(shown ? 4 : 3, tabs.Count);
    }

    /// <summary>R-13: a blue-green deployment written by hand names its migrations directory, which is a database as well.</summary>
    [Theory]
    [InlineData("  BG_MIGRATIONS_DIR: \"$(WORKSPACE)/src/Back/Data/Migrations\"", true)]
    [InlineData("  BG_MIGRATIONS_DIR: \"\"", false)]
    [InlineData("  APPLICATION_HAS_DATABASE: true", true)]
    [InlineData("  OTHER_APPLICATION_HAS_DATABASE: \"true\"", false)]
    public void DeclaresDatabase_ReadsTheVariablesOfAPipeline(string line, bool expected) =>
        Assert.Equal(expected, PipelineTemplateReferenceHelper.DeclaresDatabase($"name: Deploy\nvariables:\n{line}\n"));

    [Fact]
    public void Renders_WithProjectNoRecentData_ShowsBothEmptyHints()
    {
        _handler.SetJsonResponse("api/pipelines/runs/recent?projectId=1", new List<PipelineRunDto>());
        _handler.SetPaginatedJsonResponse<GitLightRepoDto>(HttpMethod.Get,
            "api/git/repos?page=1&pageSize=100&projectId=1&sortDescending=False", []);
        _handler.SetJsonResponse("api/releases", new PaginatedResult<ReleaseDto>());

        var cut = Render<ProjectOverviewSection>(p =>
            p.Add(x => x.Project, new ProjectDetailDto { Id = 1, Name = "Web" }));

        Assert.Contains("Information", cut.Markup);
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("NoRecentPipelines", cut.Markup);
            Assert.Contains("NoRecentCommits", cut.Markup);
            Assert.DoesNotContain("RecentActivity", cut.Markup);
            Assert.Contains("ProjectGettingStartedTitle", cut.Markup);
            Assert.Contains("project-overview-tabs", cut.Markup);
            Assert.Contains("Readme", cut.Markup);
            Assert.Contains("Changelog", cut.Markup);
            Assert.Single(cut.FindAll("table.onboarding-steps"));
            // Include Git, environment, server and pipeline readiness; no library step is needed
            // when no pipeline requires one.
            Assert.Equal(4, cut.FindAll("tr[data-testid='onboarding-step']").Count);
            Assert.Equal(4, cut.FindAll("tr[data-testid='onboarding-step'] .omni-badge").Count);
            Assert.DoesNotContain("project-onboarding-grid", cut.Markup);
            Assert.Contains("git-repositories?projectId=1&amp;create=true", cut.Markup);
            Assert.Contains("environments/new?projectId=1", cut.Markup);
            // Scoped to the checklist: /projects/1/servers is also a section tile href.
            Assert.Single(cut.FindAll("tr[data-testid='onboarding-step'] a[href='/projects/1/servers']"));
            Assert.DoesNotContain("variable-libraries/new?projectId=1", cut.Markup);
            Assert.Contains("pipelines/setup?projectId=1", cut.Markup);
            // No Overview tile on the Overview page, 13 of the 14 sections.
            Assert.Equal(13, cut.FindAll("a.omni-nav-tile").Count);
            Assert.Equal(13, cut.FindAll(".omni-nav-tile").Count);
            Assert.Empty(cut.FindAll("a.omni-nav-tile[href='/projects/1/overview']"));
            Assert.NotNull(cut.Find("a.omni-nav-tile[href='/projects/1/quality']"));
            Assert.NotNull(cut.Find("a.omni-nav-tile[href='/projects/1/artifacts']"));
            Assert.NotNull(cut.Find("a.omni-nav-tile[href='/projects/1/environments']"));
            Assert.NotNull(cut.Find("a.omni-nav-tile[href='/projects/1/monitoring']"));
            Assert.NotNull(cut.Find("a.omni-nav-tile[href='/git-repositories?projectId=1']"));
            Assert.NotNull(cut.Find("a.omni-nav-tile[href='/projects/1/edit']"));
        });

        cut.FindAll("[role='tab']").Single(button => button.TextContent.Contains("Readme")).Click();
        cut.WaitForAssertion(() => Assert.Contains("NoProjectReadme", cut.Markup));
        cut.FindAll("[role='tab']").Single(button => button.TextContent.Contains("Changelog")).Click();
        cut.WaitForAssertion(() => Assert.Contains("NoProjectChangelog", cut.Markup));
    }

    [Fact]
    public void Renders_ReadmeAndChangelog_InReadableSurfaces()
    {
        _handler.SetJsonResponse("api/pipelines/runs/recent?projectId=4", new List<PipelineRunDto>());
        _handler.SetPaginatedJsonResponse(HttpMethod.Get,
            "api/git/repos?page=1&pageSize=100&projectId=4&sortDescending=False",
            new List<GitLightRepoDto>
            {
                new() { Id = 21, ProjectId = 4, Name = "docs", DefaultBranch = "main" }
            });
        _handler.SetJsonResponse("api/git/repos/21/commits", new PaginatedResult<GitLightCommitDto>());
        _handler.SetPaginatedJsonResponse("api/git/repos/21/tree", new List<GitLightTreeEntryDto>
        {
            new() { Name = "README.md", Path = "README.md", Type = GitTreeEntryType.Blob }
        });
        _handler.SetJsonResponse("api/git/repos/21/blob", new GitLightBlobDto
        {
            Path = "README.md",
            Content = "# Documentation\n\nReadable content",
            IsBinary = false
        });
        _handler.SetJsonResponse("api/releases", new PaginatedResult<ReleaseDto>
        {
            Items =
            [
                new ReleaseDto
                {
                    Id = 8,
                    ProjectId = 4,
                    Version = "2.0.0",
                    Status = ReleaseStatus.Published,
                    Changelog = "Improved navigation",
                    PublishedAt = new DateTime(2026, 8, 10, 10, 0, 0)
                }
            ]
        });

        var cut = Render<ProjectOverviewSection>(parameters => parameters.Add(component => component.Project,
            new ProjectDetailDto { Id = 4, Name = "Docs" }));

        cut.FindAll("[role='tab']").Single(button => button.TextContent.Contains("Readme")).Click();
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("project-readme-panel", cut.Markup);
            Assert.Contains("project-readme-content", cut.Markup);
            Assert.Contains("Documentation", cut.Markup);
        });
        cut.FindAll("[role='tab']").Single(button => button.TextContent.Contains("Changelog")).Click();
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("project-changelog-entry", cut.Markup);
            Assert.Contains("Improved navigation", cut.Markup);
        });
    }

    [Fact]
    public void Renders_InitializedProject_HidesGettingStarted()
    {
        _handler.SetJsonResponse("api/pipelines/runs/recent?projectId=2", new List<PipelineRunDto>());
        _handler.SetPaginatedJsonResponse(HttpMethod.Get,
            "api/git/repos?page=1&pageSize=100&projectId=2&sortDescending=False",
            new List<GitLightRepoDto>
            {
                new() { Id = 5, ProjectId = 2, Name = "api", DefaultBranch = "main", IsEmpty = true }
            });
        _handler.SetJsonResponse("api/releases", new PaginatedResult<ReleaseDto>());
        _handler.SetPaginatedJsonResponse("api/git/repos/5/tree", new List<GitLightTreeEntryDto>());
        // R3-67: the finished checklist was already shown its last time.
        ((InMemoryUserPreferences)Services.GetRequiredService<IUserPreferences>()).Seed(PreferenceKeys.GettingStartedClosed(2), true);

        var cut = Render<ProjectOverviewSection>(parameters => parameters.Add(component => component.Project,
            new ProjectDetailDto
            {
                Id = 2,
                Name = "API",
                EnvironmentCount = 1,
                ServerCount = 1,
                Pipelines = [new PipelineDto { Id = 9, Name = "CI" }]
            }));

        cut.WaitForAssertion(() => Assert.Contains("NoRecentCommits", cut.Markup));
        // LibraryCount stays 0 here on purpose: a variable library is optional, so its row must not
        // keep the checklist alive once the project can actually run.
        Assert.DoesNotContain("ProjectGettingStartedTitle", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Renders_ServerOnlyThroughEnvironment_HidesGettingStarted()
    {
        // An environment-linked server satisfies execution readiness even without a direct project
        // server attachment.
        _handler.SetJsonResponse("api/pipelines/runs/recent?projectId=2", new List<PipelineRunDto>());
        _handler.SetPaginatedJsonResponse(HttpMethod.Get,
            "api/git/repos?page=1&pageSize=100&projectId=2&sortDescending=False",
            new List<GitLightRepoDto>
            {
                new() { Id = 5, ProjectId = 2, Name = "api", DefaultBranch = "main", IsEmpty = true }
            });
        _handler.SetJsonResponse("api/releases", new PaginatedResult<ReleaseDto>());
        _handler.SetPaginatedJsonResponse("api/git/repos/5/tree", new List<GitLightTreeEntryDto>());
        // R3-67: the finished checklist was already shown its last time.
        ((InMemoryUserPreferences)Services.GetRequiredService<IUserPreferences>()).Seed(PreferenceKeys.GettingStartedClosed(2), true);

        var cut = Render<ProjectOverviewSection>(parameters => parameters.Add(component => component.Project,
            new ProjectDetailDto
            {
                Id = 2,
                Name = "API",
                EnvironmentCount = 1,
                ServerCount = 0,
                EnvironmentServerCount = 1,
                Pipelines = [new PipelineDto { Id = 9, Name = "CI" }]
            }));

        cut.WaitForAssertion(() => Assert.Contains("NoRecentCommits", cut.Markup));
        Assert.DoesNotContain("ProjectGettingStartedTitle", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Renders_ProjectWithoutServer_KeepsGettingStarted()
    {
        _handler.SetJsonResponse("api/pipelines/runs/recent?projectId=2", new List<PipelineRunDto>());
        _handler.SetPaginatedJsonResponse(HttpMethod.Get,
            "api/git/repos?page=1&pageSize=100&projectId=2&sortDescending=False",
            new List<GitLightRepoDto>
            {
                new() { Id = 5, ProjectId = 2, Name = "api", DefaultBranch = "main", IsEmpty = true }
            });
        _handler.SetJsonResponse("api/releases", new PaginatedResult<ReleaseDto>());
        _handler.SetPaginatedJsonResponse("api/git/repos/5/tree", new List<GitLightTreeEntryDto>());

        var cut = Render<ProjectOverviewSection>(parameters => parameters.Add(component => component.Project,
            new ProjectDetailDto
            {
                Id = 2,
                Name = "API",
                EnvironmentCount = 1,
                LibraryCount = 1,
                Pipelines = [new PipelineDto { Id = 9, Name = "CI" }]
            }));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("ProjectGettingStartedTitle", cut.Markup, StringComparison.Ordinal);
            // Only the server row is still to do; the library row already links to the project's list.
            Assert.Single(cut.FindAll("tr[data-testid='onboarding-step'] a[href='/projects/2/servers']"));
            Assert.Single(cut.FindAll("tr[data-testid='onboarding-step'] a[href='/projects/2/libraries']"));
        });
    }

    [Fact]
    public void AddGitRepositoryButton_OpensProjectScopedCreationFlow()
    {
        _handler.SetJsonResponse("api/pipelines/runs/recent?projectId=2", new List<PipelineRunDto>());
        _handler.SetPaginatedJsonResponse<GitLightRepoDto>(HttpMethod.Get,
            "api/git/repos?page=1&pageSize=100&projectId=2&sortDescending=False", []);
        _handler.SetJsonResponse("api/releases", new PaginatedResult<ReleaseDto>());
        var cut = Render<ProjectOverviewSection>(parameters => parameters.Add(component => component.Project,
            new ProjectDetailDto { Id = 2, Name = "API" }));

        cut.WaitForAssertion(() => Assert.Contains("AddGitRepository", cut.Markup));
        cut.FindAll("button").Single(button => button.Names().Contains("AddGitRepository")).Click();

        Assert.EndsWith("/git-repositories?projectId=2&create=true",
            Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PermissionsLoadedAfterMount_RevealsAddGitRepositoryButton()
    {
        _handler.SetJsonResponse("api/pipelines/runs/recent?projectId=2", new List<PipelineRunDto>());
        _handler.SetPaginatedJsonResponse<GitLightRepoDto>(HttpMethod.Get,
            "api/git/repos?page=1&pageSize=100&projectId=2&sortDescending=False", []);
        _handler.SetJsonResponse("api/releases", new PaginatedResult<ReleaseDto>());
        var permissions = Services.GetRequiredService<Aetheus.Front.Components.Shared.PermissionService>();
        permissions.Clear();

        var cut = Render<ProjectOverviewSection>(parameters => parameters.Add(component => component.Project,
            new ProjectDetailDto { Id = 2, Name = "API" }));

        Assert.DoesNotContain("AddGitRepository", cut.Markup, StringComparison.Ordinal);
        Assert.Equal(6, cut.FindAll("a.omni-nav-tile").Count);

        permissions.SetPermissions(
        [
            new EffectivePermissionDto
            {
                ResourceType = ResourceType.Project,
                ResourceId = 2,
                Permission = Permission.Write
            }
        ], isAdmin: false);

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("AddGitRepository", cut.Markup, StringComparison.Ordinal);
            Assert.Equal(7, cut.FindAll("a.omni-nav-tile").Count);
            Assert.NotNull(cut.Find("a.omni-nav-tile[href='/projects/2/edit']"));
            Assert.Empty(cut.FindAll("a.omni-nav-tile[href='/projects/2/pipelines']"));
        });
    }

    [Fact]
    public void Renders_InternalRepositoryAsLinkedRepository()
    {
        _handler.SetJsonResponse("api/pipelines/runs/recent?projectId=3", new List<PipelineRunDto>());
        _handler.SetPaginatedJsonResponse(HttpMethod.Get,
            "api/git/repos?page=1&pageSize=100&projectId=3&sortDescending=False",
            new List<GitLightRepoDto>
            {
                new()
                {
                    Id = 17,
                    ProjectId = 3,
                    Name = "portfolio",
                    DefaultBranch = "main",
                    CloneUrl = "https://git.example/portfolio.git"
                }
            });
        _handler.SetJsonResponse("api/git/repos/17/commits", new PaginatedResult<GitLightCommitDto>());
        _handler.SetPaginatedJsonResponse("api/git/repos/17/tree", new List<GitLightTreeEntryDto>());
        _handler.SetJsonResponse("api/releases", new PaginatedResult<ReleaseDto>());

        var cut = Render<ProjectOverviewSection>(parameters => parameters.Add(component => component.Project,
            new ProjectDetailDto { Id = 3, Name = "Portfolio" }));

        cut.WaitForAssertion(() =>
        {
            Assert.DoesNotContain("NoRepository", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("href=\"/git-repositories/17\"", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("portfolio", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("https://git.example/portfolio.git", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("main", cut.Markup, StringComparison.Ordinal);
        });

        // The clone address uses the OE read-only copyable field, whose internal scrolling prevents
        // the card from widening.
        var cloneUrl = cut.Find("input#project-overview-clone-url");
        Assert.Equal("https://git.example/portfolio.git", cloneUrl.GetAttribute("value"));
        Assert.True(cloneUrl.HasAttribute("readonly"));
        Assert.Equal("RepositoryUrl", cloneUrl.GetAttribute("aria-label"));
        Assert.NotNull(cut.Find(".overview-clone-url.omni-text-box-field--copy button"));
        Assert.Empty(cut.FindAll("a.pipeline-name-link[href='https://git.example/portfolio.git']"));
    }

    [Fact]
    public async Task PipelineRunStarted_ForProjectPipeline_ReloadsRecentRuns()
    {
        _handler.SetJsonResponse("api/pipelines/runs/recent?projectId=1", new List<PipelineRunDto>());
        _handler.SetPaginatedJsonResponse<GitLightRepoDto>(HttpMethod.Get,
            "api/git/repos?page=1&pageSize=100&projectId=1&sortDescending=False", []);
        _handler.SetJsonResponse("api/releases", new PaginatedResult<ReleaseDto>());
        var project = new ProjectDetailDto
        {
            Id = 1,
            Name = "Web",
            Pipelines = [new PipelineDto { Id = 7, Name = "Release" }]
        };
        var cut = Render<ProjectOverviewSection>(parameters => parameters.Add(component => component.Project, project));
        cut.WaitForAssertion(() => Assert.Contains("NoRecentPipelines", cut.Markup));

        _handler.SetJsonResponse("api/pipelines/runs/recent?projectId=1", new List<PipelineRunDto>
        {
            new() { Id = 43, BuildNumber = 43, PipelineId = 7, ProjectId = 1, PipelineName = "Release", Status = PipelineStatus.Running }
        });
        var method = typeof(ProjectOverviewSection).GetMethod("OnPipelineRunStarted", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [43, 7])!);

        cut.WaitForAssertion(() => Assert.Contains(">#43<", cut.Markup));
    }

    [Fact]
    public void R30_InformationCard_ShowsStatusGradeCreatedAndUpdatedAsFourSeparateItems()
    {
        // R-30: one item per fact, each with its own term; nothing joined by a separator.
        _handler.SetJsonResponse("api/pipelines/runs/recent?projectId=2", new List<PipelineRunDto>());
        _handler.SetPaginatedJsonResponse(HttpMethod.Get,
            "api/git/repos?page=1&pageSize=100&projectId=2&sortDescending=False",
            new List<GitLightRepoDto>());
        _handler.SetJsonResponse("api/releases", new PaginatedResult<ReleaseDto>());

        var cut = Render<ProjectOverviewSection>(parameters => parameters.Add(component => component.Project,
            new ProjectDetailDto
            {
                Id = 2,
                Name = "API",
                EnvironmentCount = 1,
                ServerCount = 1,
                CreatedAt = new DateTime(2026, 1, 2, 10, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 3, 4, 11, 0, 0, DateTimeKind.Utc),
                LatestGateGrade = AnalysisGrade.B,
                Pipelines = [new PipelineDto { Id = 9, Name = "CI" }]
            }));

        var infoCard = cut.Find(".overview-info-card");
        var items = infoCard.QuerySelectorAll(".omni-description-list__item");
        Assert.Equal(4, items.Length);
        Assert.Equal(["Status", "AnalysisGrade", "Created", "Updated"],
            items.Select(item => item.QuerySelector("dt")!.TextContent.Trim()).ToArray());
        Assert.DoesNotContain("·", infoCard.TextContent, StringComparison.Ordinal);
        Assert.Contains("2026", items[2].QuerySelector("dd")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("2026", items[3].QuerySelector("dd")!.TextContent, StringComparison.Ordinal);
        Assert.NotEqual(items[2].QuerySelector("dd")!.TextContent, items[3].QuerySelector("dd")!.TextContent);
        // The grade still links to the quality page: splitting the row must not cost the link.
        Assert.Single(infoCard.QuerySelectorAll("a[href='/projects/2/quality']"));
    }

    [Theory]
    [InlineData(true, true, new[] { "DefaultBranch", "LastGitUpdate" })]
    [InlineData(true, false, new[] { "DefaultBranch" })]
    [InlineData(false, true, new[] { "LastGitUpdate" })]
    public void R30_RepositoryCard_ShowsOneItemPerFact_AnAbsentValueHidesOnlyItsItem(
        bool withBranch, bool withGitUpdate, string[] expectedTerms)
    {
        _handler.SetJsonResponse("api/pipelines/runs/recent?projectId=2", new List<PipelineRunDto>());
        _handler.SetPaginatedJsonResponse(HttpMethod.Get,
            "api/git/repos?page=1&pageSize=100&projectId=2&sortDescending=False",
            new List<GitLightRepoDto>());
        _handler.SetJsonResponse("api/releases", new PaginatedResult<ReleaseDto>());

        var cut = Render<ProjectOverviewSection>(parameters => parameters.Add(component => component.Project,
            new ProjectDetailDto
            {
                Id = 2,
                Name = "API",
                RepositoryUrl = "https://example/repo",
                DefaultBranch = withBranch ? "trunk-x" : null,
                LastGitUpdateAt = withGitUpdate ? DateTime.UtcNow.AddHours(-3) : null,
                Pipelines = [new PipelineDto { Id = 9, Name = "CI" }]
            }));

        var repositoryCard = cut.FindAll(".overview-info-card")[1];
        var terms = repositoryCard.QuerySelectorAll(".omni-description-list__item dt")
            .Select(term => term.TextContent.Trim())
            .Where(term => term is "DefaultBranch" or "LastGitUpdate")
            .ToArray();
        Assert.Equal(expectedTerms, terms);
        Assert.DoesNotContain("·", repositoryCard.TextContent, StringComparison.Ordinal);
        var branchItem = repositoryCard.QuerySelectorAll(".omni-description-list__item")
            .FirstOrDefault(item => item.QuerySelector("dt")!.TextContent.Trim() == "DefaultBranch");
        if (withBranch)
            Assert.Equal("trunk-x", branchItem!.QuerySelector("dd")!.TextContent.Trim());
        else
            Assert.Null(branchItem);
    }

    [Fact]
    public void R2_024_TheGrade_OpensTheLatestCandidateRunItWasReadFrom()
    {
        _handler.SetJsonResponse("api/pipelines/runs/recent?projectId=2", new List<PipelineRunDto>());
        _handler.SetPaginatedJsonResponse(HttpMethod.Get,
            "api/git/repos?page=1&pageSize=100&projectId=2&sortDescending=False",
            new List<GitLightRepoDto>());
        _handler.SetJsonResponse("api/releases", new PaginatedResult<ReleaseDto>());

        var cut = Render<ProjectOverviewSection>(parameters => parameters.Add(component => component.Project,
            new ProjectDetailDto
            {
                Id = 2,
                Name = "API",
                LatestGateGrade = AnalysisGrade.D,
                LatestGateGradeRunId = 2478,
                LatestGateGradeQualification = new AnalysisQualificationDto
                {
                    State = AnalysisQualificationState.DeployedSealed,
                    ReleaseVersion = "1.5.30",
                    PipelineRunId = 2478,
                    PipelineRunNumber = 339
                },
                Pipelines = [new PipelineDto { Id = 9, Name = "CI" }]
            }));

        var link = cut.Find(".overview-info-card a.project-grade-run-link");
        // The grade opens the run that sealed the deployed release on its Gate tab, where an F gives its cause.
        Assert.Equal("/pipelines/runs/2478?projectId=2&tab=gate", link.GetAttribute("href"));
        // R-12, R-20: the tooltip names the deployed release and the run, not "the latest candidate".
        Assert.Equal("ProjectGradeDeployedRelease", link.GetAttribute("title"));
        Assert.Empty(cut.FindAll(".overview-info-card a[href='/projects/2/quality']"));
    }

    private IRenderedComponent<ProjectOverviewSection> RenderOverviewWithGrade(
        AnalysisGrade? grade, AnalysisGradeCause? cause, string? pipelineName = "Candidate")
    {
        _handler.SetJsonResponse("api/pipelines/runs/recent?projectId=2", new List<PipelineRunDto>());
        _handler.SetPaginatedJsonResponse(HttpMethod.Get,
            "api/git/repos?page=1&pageSize=100&projectId=2&sortDescending=False",
            new List<GitLightRepoDto>());
        _handler.SetJsonResponse("api/releases", new PaginatedResult<ReleaseDto>());

        return Render<ProjectOverviewSection>(parameters => parameters.Add(component => component.Project,
            new ProjectDetailDto
            {
                Id = 2,
                Name = "Aetheus",
                LatestGateGrade = grade,
                LatestGateGradeRunId = 2478,
                LatestGateGradeRunNumber = 345,
                LatestGateGradeCause = cause,
                LatestGateGradePipelineId = 12,
                LatestGateGradePipelineName = pipelineName,
                LatestGateGradeQualification = new AnalysisQualificationDto
                {
                    State = AnalysisQualificationState.DeployedSealed,
                    ReleaseVersion = "1.6.5",
                    PipelineRunId = 2478,
                    PipelineRunNumber = 345
                },
                Pipelines = [new PipelineDto { Id = 9, Name = "CI" }]
            }));
    }

    // User decision 2026-11-10: below A, the grade says in parentheses what lowers it.
    [Fact]
    public void AGradeLoweredByAPipelineRun_NamesThePipelineAndItsRun_AsALinkToTheRun()
    {
        var cut = RenderOverviewWithGrade(AnalysisGrade.B, AnalysisGradeCause.Pipeline);

        var cause = cut.Find(".overview-info-card [data-testid='project-grade-cause']");
        Assert.Equal("pipeline", cause.GetAttribute("data-cause"));
        // In parentheses: the opening one before the icon and the link, the closing one after (the icon's own name is its label).
        var text = System.Text.RegularExpressions.Regex.Replace(cause.TextContent, @"\s+", string.Empty);
        Assert.StartsWith("(", text, StringComparison.Ordinal);
        Assert.EndsWith("Candidate#345)", text, StringComparison.Ordinal);
        var link = cause.QuerySelector("a")!;
        Assert.Equal("/pipelines/runs/2478?projectId=2&tab=gate", link.GetAttribute("href"));
        Assert.Equal("Candidate #345", link.TextContent.Trim());
        Assert.NotNull(cause.QuerySelector("svg"));
    }

    [Fact]
    public void AGradeLoweredByTheProject_NamesTheProject_AsALinkToItsQualityPage()
    {
        var cut = RenderOverviewWithGrade(AnalysisGrade.D, AnalysisGradeCause.Project);

        var cause = cut.Find(".overview-info-card [data-testid='project-grade-cause']");
        Assert.Equal("project", cause.GetAttribute("data-cause"));
        var link = cause.QuerySelector("a")!;
        Assert.Equal("/projects/2/quality", link.GetAttribute("href"));
        Assert.Equal("Aetheus", link.TextContent.Trim());
        Assert.NotNull(cause.QuerySelector("svg"));
    }

    [Fact]
    public void AGradeLoweredByAPipelineRunOfUnknownPipeline_NamesTheRunAlone()
    {
        var cut = RenderOverviewWithGrade(AnalysisGrade.B, AnalysisGradeCause.Pipeline, pipelineName: null);

        Assert.Equal("#345", cut.Find("[data-testid='project-grade-cause'] a").TextContent.Trim());
    }

    [Theory]
    [InlineData(AnalysisGrade.A, AnalysisGradeCause.Pipeline)]
    [InlineData(AnalysisGrade.A, null)]
    [InlineData(AnalysisGrade.B, null)]
    [InlineData(null, AnalysisGradeCause.Project)]
    public void AnAGrade_OrAGradeWithoutCause_ShowsNothingMore(AnalysisGrade? grade, AnalysisGradeCause? cause)
    {
        var cut = RenderOverviewWithGrade(grade, cause);

        Assert.Empty(cut.FindAll("[data-testid='project-grade-cause']"));
    }

    [Theory]
    [InlineData(AnalysisQualificationState.DeployedLastKnownGrade, true, "ProjectGradeLastKnown")]
    [InlineData(AnalysisQualificationState.DeployedSealed, false, "ProjectGradeDeployedRelease")]
    public void R12_R20_ALastKnownGrade_IsShownMarkedOld_AndOpensTheRunItComesFrom(
        AnalysisQualificationState state, bool expectedOld, string expectedTitle)
    {
        _handler.SetJsonResponse("api/pipelines/runs/recent?projectId=2", new List<PipelineRunDto>());
        _handler.SetPaginatedJsonResponse(HttpMethod.Get,
            "api/git/repos?page=1&pageSize=100&projectId=2&sortDescending=False",
            new List<GitLightRepoDto>());
        _handler.SetJsonResponse("api/releases", new PaginatedResult<ReleaseDto>());

        var cut = Render<ProjectOverviewSection>(parameters => parameters.Add(component => component.Project,
            new ProjectDetailDto
            {
                Id = 2,
                Name = "API",
                LatestGateGrade = AnalysisGrade.F,
                LatestGateGradeRunId = 3092,
                LatestGateGradeQualification = new AnalysisQualificationDto
                {
                    State = state,
                    ReleaseVersion = "1.5.31",
                    KnownReleaseVersion = "1.5.30",
                    PipelineRunId = 3092,
                    PipelineRunNumber = 339
                },
                Pipelines = [new PipelineDto { Id = 9, Name = "CI" }]
            }));

        var link = cut.Find(".overview-info-card a.project-grade-run-link");
        Assert.Equal("/pipelines/runs/3092?projectId=2&tab=gate", link.GetAttribute("href"));
        Assert.Equal(expectedTitle, link.GetAttribute("title"));
        Assert.Equal("F", link.QuerySelector(".grade-badge--f")!.TextContent.Trim());
        Assert.Equal(expectedOld ? 1 : 0, link.QuerySelectorAll("[data-testid='grade-badge-old']").Length);
    }

    [Theory]
    [InlineData(AnalysisQualificationState.NothingDeployed, "ProjectGradeNothingDeployed")]
    [InlineData(AnalysisQualificationState.DeployedWithoutVerdict, "ProjectGradeDeployedWithoutVerdict")]
    public void R12_R20_WithoutADeployedVerdict_TheBadgeShowsNoLetter_AndItsTooltipSaysWhy(
        AnalysisQualificationState state, string expectedTitle)
    {
        _handler.SetJsonResponse("api/pipelines/runs/recent?projectId=2", new List<PipelineRunDto>());
        _handler.SetPaginatedJsonResponse(HttpMethod.Get,
            "api/git/repos?page=1&pageSize=100&projectId=2&sortDescending=False",
            new List<GitLightRepoDto>());
        _handler.SetJsonResponse("api/releases", new PaginatedResult<ReleaseDto>());

        var cut = Render<ProjectOverviewSection>(parameters => parameters.Add(component => component.Project,
            new ProjectDetailDto
            {
                Id = 2,
                Name = "API",
                LatestGateGrade = null,
                // The analysis ran on a candidate of run 2478 whatever the release state: it must not become a link or a letter.
                LatestGateGradeRunId = 2478,
                LatestGateGradeQualification = new AnalysisQualificationDto { State = state, ReleaseVersion = "1.5.30" },
                Pipelines = [new PipelineDto { Id = 9, Name = "CI" }]
            }));

        var link = cut.Find(".overview-info-card a[href='/projects/2/quality']");
        Assert.Equal(expectedTitle, link.GetAttribute("title"));
        Assert.Empty(link.QuerySelectorAll(".grade-badge--a"));
        Assert.Single(link.QuerySelectorAll(".grade-badge--none"));
    }

    [Fact]
    public void APipelineWaitingForAMissingLibrary_PutsTheLibraryAfterThePipeline_AndWarns()
    {
        // A required library follows the pipeline readiness step, and the Information card
        // identifies the unmet requirement and its action.
        _handler.SetJsonResponse("api/pipelines/runs/recent?projectId=3", new List<PipelineRunDto>());
        _handler.SetPaginatedJsonResponse<GitLightRepoDto>(HttpMethod.Get,
            "api/git/repos?page=1&pageSize=100&projectId=3&sortDescending=False", []);
        _handler.SetJsonResponse("api/releases", new PaginatedResult<ReleaseDto>());
        _handler.SetJsonResponse("api/pipelines/setup/unmet", new List<UnmetRequirementDto>
        {
            new() { Kind = "library", Name = "aetheus-prod-host", Pipelines = ["app-prod", "app-qa"] }
        });

        var cut = Render<ProjectOverviewSection>(p =>
            p.Add(x => x.Project, new ProjectDetailDto
            {
                Id = 3,
                Name = "Web",
                Pipelines = [new PipelineDto { Id = 9, Name = "app-prod" }]
            }));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='unmet-requirements']")));
        var warning = cut.Find("[data-testid='unmet-requirements']");
        Assert.Contains("aetheus-prod-host", warning.TextContent, StringComparison.Ordinal);
        Assert.Contains("app-prod, app-qa", warning.TextContent, StringComparison.Ordinal);

        var rows = cut.FindAll("tr[data-testid='onboarding-step']");
        Assert.Equal(5, rows.Count);
        var pipelineRow = rows.ToList().FindIndex(row => row.TextContent.Contains("CreatePipeline", StringComparison.Ordinal));
        var libraryRow = rows.ToList().FindIndex(row => row.TextContent.Contains("CreateVariableLibrary", StringComparison.Ordinal));
        Assert.True(pipelineRow >= 0 && libraryRow > pipelineRow, $"pipeline row {pipelineRow}, library row {libraryRow}");
    }

    [Fact]
    public void RecentCommitRowControls_DoNotReachTheRow()
    {
        // SITE-FUNC-HOME-CAPABILITY-001: a link, button or form control in a clickable row stops click and
        // Enter/Space, or the row click runs after its own and wins the navigation.
        _handler.SetJsonResponse("api/pipelines/runs/recent?projectId=1", new List<PipelineRunDto>());
        _handler.SetPaginatedJsonResponse(HttpMethod.Get,
            "api/git/repos?page=1&pageSize=100&projectId=1&sortDescending=False",
            new List<GitLightRepoDto> { new() { Id = 3, ProjectId = 1, Name = "web", DefaultBranch = "main" } });
        _handler.SetJsonResponse("api/git/repos/3/commits", new PaginatedResult<GitLightCommitDto>
        {
            Items = [new() { Sha = "abcdef123456", ShortSha = "abcdef1", Message = "Improve overview", AuthorName = "Alice", AuthorDate = new DateTime(2026, 1, 2, 9, 0, 0) }],
            TotalCount = 1
        });
        _handler.SetJsonResponse("api/releases", new PaginatedResult<ReleaseDto>());
        _handler.SetPaginatedJsonResponse("api/git/repos/3/tree", new List<GitLightTreeEntryDto>());

        var cut = Render<ProjectOverviewSection>(p =>
            p.Add(x => x.Project, new ProjectDetailDto { Id = 1, Name = "Web", DefaultBranch = "main" }));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(RowEventIsolation.ClickableRows)));

        Assert.True(RowEventIsolation.AssertNoRowControlReachesTheRow(cut.FindAll(RowEventIsolation.ClickableRows)) >= 1);
    }

    private void StubInitializedProjectReads(int projectId)
    {
        _handler.SetJsonResponse($"api/pipelines/runs/recent?projectId={projectId}", new List<PipelineRunDto>());
        _handler.SetPaginatedJsonResponse(HttpMethod.Get,
            $"api/git/repos?page=1&pageSize=100&projectId={projectId}&sortDescending=False",
            new List<GitLightRepoDto>
            {
                new() { Id = 5, ProjectId = projectId, Name = "api", DefaultBranch = "main", IsEmpty = true }
            });
        _handler.SetJsonResponse("api/releases", new PaginatedResult<ReleaseDto>());
        _handler.SetPaginatedJsonResponse("api/git/repos/5/tree", new List<GitLightTreeEntryDto>());
    }

    private static ProjectDetailDto Project(int id, int serverCount) => new()
    {
        Id = id,
        Name = "Wordmorrow",
        EnvironmentCount = 1,
        ServerCount = serverCount,
        Pipelines = [new PipelineDto { Id = 9, Name = "CI" }]
    };

    [Fact]
    public void R3_67_AFinishedChecklist_IsShownOneLastTime_SayingSo_ThenNeverAgain()
    {
        StubInitializedProjectReads(7);
        var preferences = (InMemoryUserPreferences)Services.GetRequiredService<IUserPreferences>();

        var first = Render<ProjectOverviewSection>(p => p.Add(x => x.Project, Project(7, serverCount: 1)));

        first.WaitForAssertion(() => Assert.Contains("NoRecentCommits", first.Markup));
        Assert.Contains("ProjectGettingStartedTitle", first.Markup, StringComparison.Ordinal);
        Assert.Contains("ProjectGettingStartedDoneDescription", first.Markup, StringComparison.Ordinal);
        Assert.All(first.FindAll("tr[data-testid='onboarding-step'] .omni-badge"), badge => Assert.Contains("Done", badge.TextContent));
        Assert.True(preferences.Read<bool>(PreferenceKeys.GettingStartedClosed(7)));

        var next = Render<ProjectOverviewSection>(p => p.Add(x => x.Project, Project(7, serverCount: 1)));

        next.WaitForAssertion(() => Assert.Contains("NoRecentCommits", next.Markup));
        Assert.DoesNotContain("ProjectGettingStartedTitle", next.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void R71_TheLastShowOfAFinishedChecklist_HasADoneButton_ThatHidesItForGood()
    {
        StubInitializedProjectReads(7);
        var preferences = (InMemoryUserPreferences)Services.GetRequiredService<IUserPreferences>();
        var cut = Render<ProjectOverviewSection>(p => p.Add(x => x.Project, Project(7, serverCount: 1)));
        cut.WaitForAssertion(() => Assert.Contains("NoRecentCommits", cut.Markup));

        var done = cut.Find("[data-testid='onboarding-done']");
        Assert.Equal("Done", done.TextContent.Trim());

        done.Click();

        Assert.DoesNotContain("ProjectGettingStartedTitle", cut.Markup, StringComparison.Ordinal);
        Assert.True(preferences.Read<bool>(PreferenceKeys.GettingStartedClosed(7)));
    }

    [Fact]
    public void R71_AChecklistWhoseLibraryStepIsStillToDo_IsNotFinished_AndStaysOpen()
    {
        // Seen on the local instance (2026-10-25): every step done but « Créer une librairie » still « À faire »,
        // yet the checklist offered Done and wrote its closing preference.
        StubInitializedProjectReads(7);
        _handler.SetJsonResponse("api/pipelines/setup/unmet", new List<UnmetRequirementDto>
        {
            new() { Kind = "library", Name = "app-prod-host", Pipelines = ["app-prod"] }
        });
        var preferences = (InMemoryUserPreferences)Services.GetRequiredService<IUserPreferences>();

        var cut = Render<ProjectOverviewSection>(p => p.Add(x => x.Project, Project(7, serverCount: 1)));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='unmet-requirements']")));

        Assert.Contains("ProjectGettingStartedDescription", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid='onboarding-done']"));
        Assert.False(preferences.Read<bool>(PreferenceKeys.GettingStartedClosed(7)));
    }

    [Fact]
    public void R71_AnUnfinishedChecklist_HasNoDoneButton()
    {
        StubInitializedProjectReads(7);
        var cut = Render<ProjectOverviewSection>(p => p.Add(x => x.Project, Project(7, serverCount: 0)));
        cut.WaitForAssertion(() => Assert.Contains("NoRecentCommits", cut.Markup));

        Assert.Contains("ProjectGettingStartedTitle", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid='onboarding-done']"));
    }

    [Fact]
    public void R32_TheStepsThatCreateSomething_ComeBackToThisPage()
    {
        StubInitializedProjectReads(7);
        var nav = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        nav.NavigateTo("/projects/7/overview");
        var project = Project(7, serverCount: 0) with { EnvironmentCount = 0, LibraryCount = 0 };

        var cut = Render<ProjectOverviewSection>(p => p.Add(x => x.Project, project));
        cut.WaitForAssertion(() => Assert.Contains("NoRecentCommits", cut.Markup));

        var links = cut.FindAll("tr[data-testid='onboarding-step'] a.onboarding-step-link").Select(a => a.GetAttribute("href")).ToList();
        Assert.Contains(links, href => href == "/environments/new?projectId=7&returnUrl=%2Fprojects%2F7%2Foverview");
    }

    [Fact]
    public void R33_AnUnfinishedChecklist_CanBeFinished_AndNeverComesBack()
    {
        StubInitializedProjectReads(7);
        var preferences = (InMemoryUserPreferences)Services.GetRequiredService<IUserPreferences>();
        var cut = Render<ProjectOverviewSection>(p => p.Add(x => x.Project, Project(7, serverCount: 0)));
        cut.WaitForAssertion(() => Assert.Contains("NoRecentCommits", cut.Markup));

        var finish = cut.Find("[data-testid='onboarding-finish']");
        Assert.Equal("GettingStartedFinish", finish.TextContent.Trim());
        finish.Click();

        Assert.DoesNotContain("ProjectGettingStartedTitle", cut.Markup, StringComparison.Ordinal);
        Assert.True(preferences.Read<bool>(PreferenceKeys.GettingStartedFinished(7)));
        Assert.False(preferences.Read<bool>(PreferenceKeys.GettingStartedClosed(7)));

        var next = Render<ProjectOverviewSection>(p => p.Add(x => x.Project, Project(7, serverCount: 0)));
        next.WaitForAssertion(() => Assert.Contains("NoRecentCommits", next.Markup));
        Assert.DoesNotContain("ProjectGettingStartedTitle", next.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void R33_AProjectWhoseOnlyPipelineIsTheExternalSecurityAnalysis_AsksOnlyForTheRepositoryAndThePipeline()
    {
        StubInitializedProjectReads(7);
        var project = new ProjectDetailDto
        {
            Id = 7,
            Name = "External",
            Pipelines = [new PipelineDto { Id = 9, Name = "Security", YamlDefinition = "name: Security\nextends: 'application-security-external@3'\n" }]
        };

        var cut = Render<ProjectOverviewSection>(p => p.Add(x => x.Project, project));
        cut.WaitForAssertion(() => Assert.Contains("NoRecentCommits", cut.Markup));

        // No environment, no server: the list is finished with the repository and the pipeline.
        Assert.Equal(2, cut.FindAll("tr[data-testid='onboarding-step']").Count);
        Assert.Contains("ProjectGettingStartedDoneDescription", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void R33_AProjectWithAnOrdinaryPipeline_StillAsksForEnvironmentAndServer()
    {
        StubInitializedProjectReads(7);
        var project = new ProjectDetailDto
        {
            Id = 7,
            Name = "App",
            Pipelines = [new PipelineDto { Id = 9, Name = "CI", YamlDefinition = "extends: application-ci\n" }]
        };

        var cut = Render<ProjectOverviewSection>(p => p.Add(x => x.Project, project));
        cut.WaitForAssertion(() => Assert.Contains("NoRecentCommits", cut.Markup));

        Assert.Equal(4, cut.FindAll("tr[data-testid='onboarding-step']").Count);
        Assert.Contains("ProjectGettingStartedDescription", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void R3_67_AnUnfinishedChecklist_SaysItGoesOnceDone_AndIsNotClosed()
    {
        StubInitializedProjectReads(7);
        var preferences = (InMemoryUserPreferences)Services.GetRequiredService<IUserPreferences>();

        var cut = Render<ProjectOverviewSection>(p => p.Add(x => x.Project, Project(7, serverCount: 0)));

        cut.WaitForAssertion(() => Assert.Contains("NoRecentCommits", cut.Markup));
        Assert.Contains("ProjectGettingStartedTitle", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("ProjectGettingStartedDescription", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("ProjectGettingStartedDoneDescription", cut.Markup, StringComparison.Ordinal);
        Assert.False(preferences.Values.ContainsKey(PreferenceKeys.GettingStartedClosed(7)));
    }

    /// <summary>Recette R-4: a project that names an external address and has no repository in Aetheus does not show that
    /// address as its repository URL: the card says it is an external address nothing reads, with the mirror notice.</summary>
    [Fact]
    public void R4_AnExternalAddressWithoutMirror_IsShownAsNotRead_WithTheMirrorNotice()
    {
        _handler.SetJsonResponse("api/pipelines/runs/recent?projectId=21", new List<PipelineRunDto>());
        _handler.SetPaginatedJsonResponse<GitLightRepoDto>(HttpMethod.Get,
            "api/git/repos?page=1&pageSize=100&projectId=21&sortDescending=False", []);
        _handler.SetJsonResponse("api/releases", new PaginatedResult<ReleaseDto>());

        var cut = Render<ProjectOverviewSection>(p =>
            p.Add(x => x.Project, new ProjectDetailDto { Id = 21, Name = "OE", RepositoryUrl = "https://github.com/team/app.git" }));

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("ExternalRepositoryAddressNotRead", cut.Find("input#project-overview-clone-url").GetAttribute("aria-label"));
            Assert.Contains("ExternalAddressNotMirroredHint", cut.Markup, StringComparison.Ordinal);
        });
    }
}
