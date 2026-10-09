// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using ProjectsPage = Aetheus.Front.Components.Projects.Projects;
namespace Aetheus.Front.Tests.Pages;

public class ProjectsTests : BunitContext
{
    public ProjectsTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    private readonly BunitTestHelper.TestHandler _handler;

    [Fact]
    public void Renders_ProjectList()
    {
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>
        {
            Items =
            [
                new ProjectDto
                {
                    Id = 1,
                    Name = "Web App",
                    Description = "Main web application",
                    Status = ProjectStatus.Active,
                    Tags = ["web", "dotnet"]
                }
            ],
            TotalCount = 1
        });

        var cut = Render<ProjectsPage>();
        cut.WaitForState(() => cut.Markup.Contains("Web App"), TimeSpan.FromSeconds(2));
        Assert.Contains("Web App", cut.Markup);
    }

    [Fact]
    public void Renders_Empty()
    {
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>
        {
            Items = [],
            TotalCount = 0
        });

        var cut = Render<ProjectsPage>();
        cut.WaitForState(() => cut.Markup.Contains("NoProjectsFound"), TimeSpan.FromSeconds(2));
        Assert.Contains("NoProjectsFound", cut.Markup);
    }

    [Fact]
    public void ProjectCard_RendersCommitRunAndProduction_WithoutAChildOfLine()
    {
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>
        {
            Items =
            [
                new ProjectDto
                {
                    Id = 1,
                    Name = "Operational",
                    Status = ProjectStatus.Active,
                    LastCommitId = 42,
                    LastCommitSha = "0123456789abcdef",
                    LastCommitMessage = "Ship project cards",
                    LastCommitAt = new DateTime(2026, 7, 30, 9, 0, 0, DateTimeKind.Utc),
                    LastRunId = 52,
                    LastRunName = "Deploy",
                    LastRunStatus = PipelineStatus.Success,
                    LastRunAt = new DateTime(2026, 7, 30, 9, 5, 0, DateTimeKind.Utc),
                    LatestGateGrade = AnalysisGrade.B,
                    ProductionStatus = ProjectProductionStatus.Online,
                    OnlineUserCount = 8
                }
            ],
            TotalCount = 1
        });
        var cut = Render<ProjectsPage>();
        cut.WaitForState(() => cut.Markup.Contains("Operational"), TimeSpan.FromSeconds(2));

        Assert.Contains("href=\"/git-repositories/commits/42\"", cut.Markup);
        Assert.Contains("href=\"/pipelines/runs/52\"", cut.Markup);
        // PLAN-003 lot 8 / D24: the tile names the run a person launched; there is no "child of" line.
        Assert.DoesNotContain("ChildOf", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("project-production-status online", cut.Markup);
        Assert.Contains("project-online-users", cut.Markup);
        // PLAN-003 lot 8: two ruled lines, not one framed strip: production and its state, then the
        // grade and the people online.
        var lines = cut.FindAll(".project-production > .project-production-row");
        Assert.Equal(2, lines.Count);
        Assert.NotNull(lines[0].QuerySelector(".project-production-status"));
        Assert.NotNull(lines[1].QuerySelector(".grade-badge"));
        Assert.NotNull(lines[1].QuerySelector(".project-online-users"));
        // PLAN-003 lot 18: the tile grade is the shared badge, not a hand-coloured strong.
        var grade = Assert.Single(cut.FindAll(".grade-badge"));
        Assert.Equal("B", grade.TextContent.Trim());
        Assert.Contains("omni-badge--success", grade.ClassName, StringComparison.Ordinal);
        Assert.Contains("href=\"/projects/1/quality\"", cut.Markup);
        Assert.Contains("OnlineUsers", cut.Markup);
        Assert.DoesNotContain("PipelineCount", cut.Markup);
        Assert.DoesNotContain("Repository", cut.Markup);
    }

    /// <summary>
    /// PLAN-005 lot 7: the commit message's first line on its own line (whole message in the title),
    /// the run status as a badge with its word instead of a dot, and a run in progress says so with
    /// the step it is on.
    /// </summary>
    [Fact]
    public void ProjectCard_ShowsTheCommitFirstLine_AndARunInProgressWithItsStep()
    {
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>
        {
            Items =
            [
                new ProjectDto
                {
                    Id = 1,
                    Name = "Operational",
                    Status = ProjectStatus.Active,
                    LastCommitId = 42,
                    LastCommitSha = "0123456789abcdef",
                    LastCommitMessage = "Ship project cards\n\nLong body that the tile never shows.",
                    LastRunId = 52,
                    LastRunName = "Deploy",
                    LastRunStatus = PipelineStatus.Running,
                    LastRunIsActive = true,
                    LastRunCurrentStep = "Deploy · push"
                },
                new ProjectDto
                {
                    Id = 2,
                    Name = "Quiet",
                    Status = ProjectStatus.Active,
                    LastCommitId = 43,
                    LastCommitSha = "fedcba9876543210",
                    LastRunId = 53,
                    LastRunName = "Lint",
                    LastRunStatus = PipelineStatus.Success
                }
            ],
            TotalCount = 2
        });
        var cut = Render<ProjectsPage>();
        cut.WaitForState(() => cut.Markup.Contains("Quiet"), TimeSpan.FromSeconds(2));

        var cards = cut.FindAll(".project-card");
        var active = cards.Single(card => card.TextContent.Contains("Operational"));
        var quiet = cards.Single(card => card.TextContent.Contains("Quiet"));

        var message = active.QuerySelector(".project-activity-line")!;
        Assert.Equal("Ship project cards", message.TextContent);
        Assert.Contains("Long body", message.GetAttribute("title"), StringComparison.Ordinal);
        Assert.Contains("RunInProgress", active.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("LastRun", active.TextContent, StringComparison.Ordinal);
        Assert.Contains(active.QuerySelectorAll(".project-activity-line"), line => line.TextContent == "Deploy · push");
        Assert.NotNull(active.QuerySelector(".project-activity-run .omni-badge"));
        Assert.Empty(cut.FindAll(".pipeline-run-dot"));

        // A finished run is "last run", with no step line; a commit without a message falls back to "Commit".
        Assert.Contains("LastRun", quiet.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("RunInProgress", quiet.TextContent, StringComparison.Ordinal);
        Assert.Empty(quiet.QuerySelectorAll(".project-activity-line"));
        Assert.Contains("Commit", quiet.QuerySelector(".project-activity-link")!.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectCommit_LinksToTheCommitPage_EvenWithAnInternalRepository()
    {
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>
        {
            Items =
            [
                new ProjectDto
                {
                    Id = 1,
                    Name = "Repository project",
                    Status = ProjectStatus.Active,
                    InternalRepositoryId = 7,
                    LastCommitId = 42,
                    LastCommitSha = "0123456789abcdef"
                }
            ],
            TotalCount = 1
        });

        var cut = Render<ProjectsPage>();

        // Recette R-319: the last commit may come from another repository than the internal one; the
        // commit page resolves the repository that holds it instead of guessing the first one.
        cut.WaitForAssertion(() => Assert.Contains("href=\"/git-repositories/commits/42\"", cut.Markup));
        Assert.DoesNotContain("/git-repositories/7/commits/", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void DenseView_SwitchesToCompactPortfolioList()
    {
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>
        {
            Items = [new ProjectDto { Id = 1, Name = "Dense", Status = ProjectStatus.Active }],
            TotalCount = 1
        });

        var cut = Render<ProjectsPage>();
        cut.WaitForState(() => cut.Markup.Contains("Dense"), TimeSpan.FromSeconds(2));
        cut.Find("button[title='DenseView']").Click();

        Assert.Contains("project-dense-list", cut.Markup);
        Assert.DoesNotContain("project-card-grid\"", cut.Markup);
    }

    [Fact]
    public void DenseView_ShowsTheFirstLineOfTheCommitMessage_LikeTheTiles()
    {
        // PLAN-005 lot 7: the dense list says what the last commit is, not only its SHA.
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>
        {
            Items =
            [
                new ProjectDto
                {
                    Id = 1, Name = "Dense", Status = ProjectStatus.Active,
                    LastCommitId = 5, LastCommitSha = "abcdef1234567890",
                    LastCommitMessage = "fix: keep the session\n\nlong body", LastCommitAt = DateTime.UtcNow
                }
            ],
            TotalCount = 1
        });

        var cut = Render<ProjectsPage>();
        cut.WaitForState(() => cut.Markup.Contains("Dense"), TimeSpan.FromSeconds(2));
        cut.Find("button[title='DenseView']").Click();

        var line = cut.Find(".project-dense-row .project-dense-activity .project-activity-line");
        Assert.Equal("fix: keep the session", line.TextContent);
        Assert.Equal("fix: keep the session\n\nlong body", line.GetAttribute("title"));
    }

    [Fact]
    public void FavoriteButton_PinsProjectAndPersistsPreference()
    {
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>
        {
            Items = [new ProjectDto { Id = 7, Name = "Favorite me", Status = ProjectStatus.Active }],
            TotalCount = 1
        });

        var cut = Render<ProjectsPage>();
        cut.WaitForState(() => cut.Markup.Contains("Favorite me"), TimeSpan.FromSeconds(2));
        cut.Find("button[title='AddToFavorites']").Click();

        Assert.Contains("project-favorite active", cut.Markup);
        Assert.Contains(JSInterop.Invocations, invocation =>
            invocation.Identifier == "localStorage.setItem"
            && Equals(invocation.Arguments[0], "aetheus.projects.favorites"));
    }

    [Fact]
    public async Task NewProject_OpensDialog()
    {
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto> { Items = [], TotalCount = 0 });

        var cut = Render<ProjectsPage>();
        var dialog = Services.GetRequiredService<OmniDialogService>();
        var opened = false;
        dialog.OnOpen += (_, _, _, _) => opened = true;

        // The "New project" button now opens the create dialog instead of navigating. Start the
        // (un-awaited) open, assert OnOpen fired, then close and await - the dialog never resolves
        // on its own, so we must close it to complete the captured task (no WaitForState on it).
        var method = typeof(ProjectsPage).GetMethod("OpenCreateDialog", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var task = cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [])!);
        await cut.InvokeAsync(() => dialog.Close(null));
        await task;

        Assert.True(opened);
    }

    /// <summary>Recette R2-121: a project is only its record until its pipelines exist, so creating
    /// one leads straight to the setup wizard for it, whose readiness check names what is missing.</summary>
    [Fact]
    public async Task NewProject_Created_LeadsToThePipelineSetupWizardForIt()
    {
        BunitTestHelper.UseImmediateDialogs(this);
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto> { Items = [], TotalCount = 0 });
        var dialog = (Aetheus.Front.Tests.TestDoubles.ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        dialog.OpenResult = new ProjectDto { Id = 42, Name = "Portfolio" };
        dialog.ConfirmResult = true;
        var cut = Render<ProjectsPage>();

        cut.WaitForAssertion(() => Assert.False(cut.Find(".project-list-create").HasAttribute("disabled")));
        await cut.Find(".project-list-create").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Equal("SetUpPipelines", dialog.LastConfirmOptions?.OkButtonText);
        Assert.Equal("SetUpNewProjectPipelinesConfirm", dialog.LastConfirmMessage);
        Assert.EndsWith("/pipelines/setup?projectId=42",
            Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri, StringComparison.Ordinal);
    }
}
