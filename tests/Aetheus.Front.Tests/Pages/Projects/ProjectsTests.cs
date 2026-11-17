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
        cut.WaitForState(() => cut.Markup.Contains("Web App"));
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
        cut.WaitForState(() => cut.Markup.Contains("NoProjectsFound"));
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
                    OnlineUserCount = 8,
                    OnlineUserState = OnlineAudienceState.Measured
                }
            ],
            TotalCount = 1
        });
        var cut = Render<ProjectsPage>();
        cut.WaitForState(() => cut.Markup.Contains("Operational"));

        Assert.Contains("href=\"/git-repositories/commits/42\"", cut.Markup);
        Assert.Contains("href=\"/pipelines/runs/52\"", cut.Markup);
        // The tile identifies the root run without a child-of line.
        Assert.DoesNotContain("ChildOf", cut.Markup, StringComparison.Ordinal);
        var productionBadge = Assert.Single(cut.FindAll("[data-testid=\"project-production-status\"]"));
        Assert.Contains("omni-badge", productionBadge.ClassName, StringComparison.Ordinal);
        Assert.Contains("accent", productionBadge.ClassName, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<time", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("project-online-users", cut.Markup);
        // Two ruled lines, not one framed strip: production and its state, then the grade and the
        // people online.
        var lines = cut.FindAll(".project-production > .project-production-row");
        Assert.Equal(2, lines.Count);
        Assert.NotNull(lines[0].QuerySelector("[data-testid=\"project-production-status\"]"));
        Assert.NotNull(lines[1].QuerySelector(".grade-badge"));
        Assert.NotNull(lines[1].QuerySelector(".project-online-users"));
        // The tile grade is the shared filled badge (R-107), not a hand-coloured strong.
        var grade = Assert.Single(cut.FindAll(".grade-badge"));
        Assert.Equal("B", grade.TextContent.Trim());
        Assert.Contains("grade-badge--b", grade.ClassName, StringComparison.Ordinal);
        Assert.Contains("omni-badge--solid", grade.ClassName, StringComparison.Ordinal);
        Assert.Contains("href=\"/projects/1/quality\"", cut.Markup);
        Assert.Contains("OnlineUsers", cut.Markup);
        Assert.DoesNotContain("PipelineCount", cut.Markup);
        Assert.DoesNotContain("Repository", cut.Markup);
    }

    /// <summary>R-12, R-20 (option B): the list shows a sealed letter as is, an old last known letter marked old with
    /// the tooltip naming its release, and a dash (no letter, no old mark) when nothing is known.</summary>
    [Fact]
    public void ProjectCard_Grade_ShowsSealedAsIs_LastKnownMarkedOld_AndNothingKnownAsADash()
    {
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>
        {
            Items =
            [
                new ProjectDto
                {
                    Id = 1, Name = "Sealed", Status = ProjectStatus.Active, LatestGateGrade = AnalysisGrade.B,
                    LatestGateGradeQualification = new AnalysisQualificationDto { State = AnalysisQualificationState.DeployedSealed, ReleaseVersion = "1.5.30" }
                },
                new ProjectDto
                {
                    Id = 2, Name = "Old", Status = ProjectStatus.Active, LatestGateGrade = AnalysisGrade.F,
                    LatestGateGradeQualification = new AnalysisQualificationDto
                    {
                        State = AnalysisQualificationState.DeployedLastKnownGrade, ReleaseVersion = "1.5.31", KnownReleaseVersion = "1.5.30"
                    }
                },
                new ProjectDto
                {
                    Id = 3, Name = "Nothing", Status = ProjectStatus.Active,
                    LatestGateGradeQualification = new AnalysisQualificationDto { State = AnalysisQualificationState.DeployedWithoutVerdict, ReleaseVersion = "1.4.0" }
                }
            ],
            TotalCount = 3
        });
        var cut = Render<ProjectsPage>();
        cut.WaitForState(() => cut.Markup.Contains("Nothing"));

        // The list is sorted by name, so each row is found by the sentence of its tooltip.
        var rows = cut.FindAll(".project-gate-summary").ToDictionary(row => row.GetAttribute("title")!);
        Assert.Equal(3, rows.Count);
        var sealedRow = rows["ProjectGradeDeployedRelease"];
        Assert.Equal("B", sealedRow.QuerySelector(".grade-badge--b")!.TextContent.Trim());
        Assert.Empty(sealedRow.QuerySelectorAll("[data-testid='grade-badge-old']"));
        var oldRow = rows["ProjectGradeLastKnown"];
        Assert.Equal("F", oldRow.QuerySelector(".grade-badge--f")!.TextContent.Trim());
        Assert.Single(oldRow.QuerySelectorAll("[data-testid='grade-badge-old']"));
        var nothingRow = rows["ProjectGradeDeployedWithoutVerdict"];
        Assert.Single(nothingRow.QuerySelectorAll(".grade-badge--none"));
        Assert.Empty(nothingRow.QuerySelectorAll("[data-testid='grade-badge-old']"));
    }

    /// <summary>R-13/R-23: a project whose audience measurement waits for data says so with a « ? » help, never a
    /// zero; a measured zero is a number; a project without web analytics shows no counter at all.</summary>
    [Fact]
    public void ProjectCard_OnlineCounter_TellsWaitingFromMeasuredZeroFromUnavailable()
    {
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>
        {
            Items =
            [
                new ProjectDto { Id = 1, Name = "Waiting", Status = ProjectStatus.Active, OnlineUserCount = 0, OnlineUserState = OnlineAudienceState.Waiting, OnlineUserLastEventAt = new DateTime(2026, 11, 5, 10, 0, 0, DateTimeKind.Utc) },
                new ProjectDto { Id = 2, Name = "Zero", Status = ProjectStatus.Active, OnlineUserCount = 0, OnlineUserState = OnlineAudienceState.Measured },
                new ProjectDto { Id = 3, Name = "Hidden", Status = ProjectStatus.Active, OnlineUserState = OnlineAudienceState.Unavailable }
            ],
            TotalCount = 3
        });
        var cut = Render<ProjectsPage>();
        cut.WaitForState(() => cut.Markup.Contains("Hidden"));

        var counters = cut.FindAll("[data-testid=\"online-audience\"]");
        Assert.Equal(2, counters.Count);
        Assert.Equal("Waiting", counters[0].GetAttribute("data-state"));
        // Waiting: the words, no number at all.
        Assert.NotNull(counters[0].QuerySelector("[data-testid=\"online-audience-waiting\"]"));
        Assert.Null(counters[0].QuerySelector("[data-testid=\"online-audience-count\"]"));
        Assert.Equal("Measured", counters[1].GetAttribute("data-state"));
        // The measured zero stays a count, with no waiting words.
        Assert.NotNull(counters[1].QuerySelector("[data-testid=\"online-audience-count\"]"));
        Assert.Null(counters[1].QuerySelector("[data-testid=\"online-audience-waiting\"]"));
        // Both carry the « ? » help; the waiting one adds that it is not a measured zero.
        var helps = cut.FindAll("[data-testid=\"online-audience-help\"]");
        Assert.Equal(2, helps.Count);
        Assert.Contains("OnlineUsersWaitingHint", helps[0].GetAttribute("aria-label"), StringComparison.Ordinal);
        // R-23: the waiting help carries the last data time the project DTO brought.
        Assert.Contains("OnlineUsersWaitingLast", helps[0].GetAttribute("aria-label"), StringComparison.Ordinal);
        Assert.DoesNotContain("OnlineUsersWaitingHint", helps[1].GetAttribute("aria-label"), StringComparison.Ordinal);
        Assert.Contains("OnlineUsersHelp", helps[1].GetAttribute("aria-label"), StringComparison.Ordinal);
    }

    /// <summary> The commit message's first line on its own line (whole message in the title), the
    /// run status as a badge with its word instead of a dot, and a run in progress says so with the
    /// step it is on. </summary>
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
        cut.WaitForState(() => cut.Markup.Contains("Quiet"));

        var cards = cut.FindAll(".project-card");
        // R-5: every project card is an OE card laid out by the OE tile grid.
        Assert.Equal(cards.Count, cut.FindAll(".omni-tile-grid > .project-card-item > .omni-card.project-card").Count);
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
        // R-27: the pipeline name is its own ellipsis line; a finished run has no step line after it.
        Assert.Equal(["Lint"], quiet.QuerySelectorAll(".project-activity-line").Select(line => line.TextContent));
        Assert.Equal(["Deploy", "Deploy · push"],
            active.QuerySelectorAll(".project-activity-run ~ .project-activity-ellipsis").Select(line => line.TextContent));
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

        // The last commit may come from another repository than the internal one; the commit page
        // resolves the repository that holds it instead of guessing the first one.
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
        cut.WaitForState(() => cut.Markup.Contains("Dense"));
        cut.Find("button[title='DenseView']").Click();

        Assert.Contains("project-dense-list", cut.Markup);
        Assert.DoesNotContain("omni-tile-grid", cut.Markup);
    }

    [Fact]
    public void DenseView_ShowsTheFirstLineOfTheCommitMessage_LikeTheTiles()
    {
        // The dense list says what the last commit is, not only its SHA.
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
        cut.WaitForState(() => cut.Markup.Contains("Dense"));
        cut.Find("button[title='DenseView']").Click();

        var line = cut.Find(".project-dense-row .project-dense-activity .project-activity-line");
        Assert.Equal("fix: keep the session", line.TextContent);
        Assert.Equal("fix: keep the session\n\nlong body", line.GetAttribute("title"));
    }

    private static PaginatedResult<ProjectDto> DeletionPendingProject() => new()
    {
        Items =
        [
            new ProjectDto
            {
                Id = 9, Name = "Doomed", Description = "Should not show", Status = ProjectStatus.Active,
                DeletionRequestedAt = DateTime.UtcNow,
                LastCommitId = 5, LastCommitSha = "abcdef1234567890", LastCommitMessage = "commit message",
                LastRunId = 3, LastRunName = "Deploy", LastRunStatus = PipelineStatus.Success,
                Tags = ["tag-a"]
            }
        ],
        TotalCount = 1
    };

    /// <summary>R-8: a project whose deletion is to be finished keeps its name and the badge, nothing else.</summary>
    [Fact]
    public void DeletionPendingProject_Tile_ShowsOnlyTheNameAndTheBadge()
    {
        _handler.SetJsonResponse("api/projects", DeletionPendingProject());
        var cut = Render<ProjectsPage>();
        cut.WaitForState(() => cut.Markup.Contains("Doomed"));

        var card = cut.Find(".project-card");
        Assert.NotNull(card.QuerySelector("[data-testid='project-deletion-pending']"));
        Assert.Contains("Doomed", card.TextContent, StringComparison.Ordinal);
        Assert.Empty(card.QuerySelectorAll(".project-favorite"));
        Assert.Empty(card.QuerySelectorAll(".project-production, .project-activity, .project-card-tags, .project-card-desc"));
        Assert.DoesNotContain("Should not show", card.TextContent, StringComparison.Ordinal);
        Assert.Empty(card.QuerySelectorAll("[data-testid='project-production-status']"));
    }

    [Fact]
    public void DeletionPendingProject_DenseRow_ShowsOnlyTheNameAndTheBadge()
    {
        _handler.SetJsonResponse("api/projects", DeletionPendingProject());
        var cut = Render<ProjectsPage>();
        cut.WaitForState(() => cut.Markup.Contains("Doomed"));
        cut.Find("button[title='DenseView']").Click();

        var row = cut.Find(".project-dense-row");
        Assert.NotNull(row.QuerySelector("[data-testid='project-deletion-pending']"));
        Assert.Empty(row.QuerySelectorAll(".project-favorite, .project-dense-activity, .project-dense-users, .project-dense-description"));
        Assert.Empty(row.QuerySelectorAll("[data-testid='project-production-status']"));
    }

    /// <summary>R-28: REST dates arrive as Kind Local (UTC-to-Local converter); stamping them UTC shifted
    /// them by the browser offset and past events read "in 35 min". Each kind must give the same instant.</summary>
    [Fact]
    public void AsOffset_KeepsTheInstant_WhateverTheKind()
    {
        var instant = new DateTime(2026, 7, 30, 9, 0, 0, DateTimeKind.Utc);
        var asOffset = typeof(ProjectsPage).GetMethod("AsOffset", BindingFlags.NonPublic | BindingFlags.Static)!;
        DateTimeOffset Convert(DateTime value) => (DateTimeOffset)asOffset.Invoke(null, [value])!;

        Assert.Equal(instant, Convert(instant).UtcDateTime);
        Assert.Equal(instant, Convert(instant.ToLocalTime()).UtcDateTime);
        Assert.Equal(instant, Convert(DateTime.SpecifyKind(instant, DateTimeKind.Unspecified)).UtcDateTime);
    }

    /// <summary>R-30: the second choice of the "Project created" dialog opens the project.</summary>
    [Fact]
    public async Task NewProject_Created_SecondChoiceOpensTheProject()
    {
        BunitTestHelper.UseImmediateDialogs(this);
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto> { Items = [], TotalCount = 0 });
        var dialog = (Aetheus.Front.Tests.TestDoubles.ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        dialog.OpenResult = new ProjectDto { Id = 42, Name = "Portfolio" };
        dialog.ConfirmResult = false;
        var cut = Render<ProjectsPage>();

        cut.WaitForAssertion(() => Assert.False(cut.Find(".project-list-create").HasAttribute("disabled")));
        await cut.Find(".project-list-create").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Equal("OpenProject", dialog.LastConfirmOptions?.CancelButtonText);
        Assert.EndsWith("/projects/42",
            Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri, StringComparison.Ordinal);
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
        cut.WaitForState(() => cut.Markup.Contains("Favorite me"));
        cut.Find("button[title='AddToFavorites']").Click();

        Assert.Contains("project-favorite active", cut.Markup);
        // R1-66: the favourite is the user's preference, kept on the server.
        var preferences = Services.GetRequiredService<Aetheus.Front.Tests.TestDoubles.InMemoryUserPreferences>();
        Assert.Equal([7], preferences.Read<HashSet<int>>(PreferenceKeys.ProjectFavorites));
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

    /// <summary>A project is only its record until its pipelines exist, so creating one leads
    /// straight to the setup wizard for it, whose readiness check names what is missing.</summary>
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
