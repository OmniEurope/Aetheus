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
    public void Renders_NullProject_ShowsAetheusLoader()
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
            // Use the shared grade badge; C has the warning tone.
            var grade = Assert.Single(cut.FindAll(".grade-badge"));
            Assert.Equal("C", grade.TextContent.Trim());
            Assert.Contains("omni-badge--warning", grade.ClassName, StringComparison.Ordinal);
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
            // The linked run is rendered by the hierarchy tree, which keeps the bare id (the "#"
            // prefix belongs to the grid's merged id + actions cell).
            Assert.Equal("43", cut.Find("a.pipeline-run-tree-id").TextContent.Trim());
        });
    }

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
            Assert.Equal(13, cut.FindAll(".project-section-link").Count);
            Assert.Equal(13, cut.FindAll(".project-section-tile").Count);
            Assert.Empty(cut.FindAll(".project-section-link[href='/projects/1/overview']"));
            Assert.NotNull(cut.Find(".project-section-link[href='/projects/1/quality']"));
            Assert.NotNull(cut.Find(".project-section-link[href='/projects/1/artifacts']"));
            Assert.NotNull(cut.Find(".project-section-link[href='/projects/1/environments']"));
            Assert.NotNull(cut.Find(".project-section-link[href='/projects/1/monitoring']"));
            Assert.NotNull(cut.Find(".project-section-link[href='/git-repositories?projectId=1']"));
            Assert.NotNull(cut.Find(".project-section-link[href='/projects/1/edit']"));
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
        Assert.Equal(6, cut.FindAll(".project-section-link").Count);

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
            Assert.Equal(7, cut.FindAll(".project-section-link").Count);
            Assert.NotNull(cut.Find(".project-section-link[href='/projects/2/edit']"));
            Assert.Empty(cut.FindAll(".project-section-link[href='/projects/2/pipelines']"));
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
            new() { Id = 43, PipelineId = 7, ProjectId = 1, PipelineName = "Release", Status = PipelineStatus.Running }
        });
        var method = typeof(ProjectOverviewSection).GetMethod("OnPipelineRunStarted", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [43, 7])!);

        cut.WaitForAssertion(() => Assert.Contains(">#43<", cut.Markup));
    }

    [Fact]
    public void InformationCard_GroupsStatusWithGradeAndTheTwoDates()
    {
        // Without description or tags, show status and grade on one row and dates on the next.
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
        Assert.Equal(2, infoCard.QuerySelectorAll(".omni-description-list__item").Length);
        // The grade still links to the quality page: grouping the row must not cost the link.
        Assert.Single(infoCard.QuerySelectorAll("a[href='/projects/2/quality']"));
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
                Pipelines = [new PipelineDto { Id = 9, Name = "CI" }]
            }));

        var link = cut.Find(".overview-info-card a.project-grade-run-link");
        // The grade opens the run on its Gate tab, where an F gives its cause.
        Assert.Equal("/pipelines/runs/2478?projectId=2&tab=gate", link.GetAttribute("href"));
        Assert.Equal("ProjectGradeLatestCandidate", link.GetAttribute("title"));
        Assert.Empty(cut.FindAll(".overview-info-card a[href='/projects/2/quality']"));
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
}
