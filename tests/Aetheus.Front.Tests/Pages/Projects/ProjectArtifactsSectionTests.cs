// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Projects.ProjectDetailSections;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Projects;

public class ProjectArtifactsSectionTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public ProjectArtifactsSectionTests() => _handler = BunitTestHelper.RegisterServices(this);

    private static PaginatedResult<PipelineArtifactDto> Page(params string[] names) => new()
    {
        Items = names.Select((n, i) => new PipelineArtifactDto
        {
            Id = i + 1,
            Name = n,
            ProjectId = 1,
            PipelineName = "CI",
            SizeBytes = 1024,
            RetentionPolicy = ArtifactRetentionPolicy.Build,
            CreatedAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            RetentionExpiresAt = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)
        }).ToList(),
        TotalCount = names.Length
    };

    [Fact]
    public void Renders_ArtifactsList_ShowsArtifactName()
    {
        _handler.SetJsonResponse("api/artifacts/project/1", Page("nuget-package", "docker-image"));

        var cut = Render<ProjectArtifactsSection>(p => p.Add(c => c.ProjectId, 1));

        cut.WaitForState(() => cut.Markup.Contains("nuget-package"));
        Assert.Contains("nuget-package", cut.Markup);
    }

    [Fact]
    public void Renders_EmptyList_WithoutError()
    {
        _handler.SetJsonResponse("api/artifacts/project/2", Page());

        var cut = Render<ProjectArtifactsSection>(p => p.Add(c => c.ProjectId, 2));

        Assert.DoesNotContain("nuget-package", cut.Markup);
    }

    [Fact]
    public void ProjectIdChange_ReloadsArtifactsOnSameInstance()
    {
        _handler.SetJsonResponse("api/artifacts/project/1", Page("first-artifact"));
        _handler.SetJsonResponse("api/artifacts/project/2", Page("second-artifact"));
        var cut = Render<ProjectArtifactsSection>(p => p.Add(c => c.ProjectId, 1));

        cut.Render(p => p.Add(c => c.ProjectId, 2));

        cut.WaitForAssertion(() => Assert.Contains("second-artifact", cut.Markup));
        Assert.DoesNotContain("first-artifact", cut.Markup);
    }

    [Fact]
    public async Task ColumnSort_ReachesTheProjectArtifactsApi()
    {
        _handler.SetJsonResponse("api/artifacts/project/1", Page("alpha"));
        var cut = Render<ProjectArtifactsSection>(p => p.Add(c => c.ProjectId, 1));
        var method = typeof(ProjectArtifactsSection).GetMethod("OnLoadData",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance,
            [new GridLoadArgs { Skip = 0, Top = 25, OrderBy = "Name desc" }])!);

        Assert.Contains(_handler.Requests, request =>
            request.Url.Contains("api/artifacts/project/1", StringComparison.Ordinal)
            && request.Url.Contains("sortBy=Name", StringComparison.Ordinal)
            && request.Url.Contains("sortDescending=true", StringComparison.Ordinal));
    }

    [Fact]
    public void RowControls_DoNotReachTheRow()
    {
        // SITE-FUNC-HOME-CAPABILITY-001: a link, button or form control in a clickable row stops click and
        // Enter/Space, or the row click runs after its own and wins the navigation.
        _handler.SetJsonResponse("api/artifacts/project/1", new PaginatedResult<PipelineArtifactDto>
        {
            Items =
            [
                new PipelineArtifactDto
                {
                    Id = 1,
                    Name = "nuget-package",
                    ProjectId = 1,
                    PipelineName = "CI",
                    PipelineRunId = 4,
                    SizeBytes = 1024,
                    RetentionPolicy = ArtifactRetentionPolicy.Build,
                    CreatedAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)
                }
            ],
            TotalCount = 1
        });

        var cut = Render<ProjectArtifactsSection>(p => p.Add(c => c.ProjectId, 1));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(RowEventIsolation.ClickableRows)));

        // The download button, the artifact link and the run link.
        Assert.True(RowEventIsolation.AssertNoRowControlReachesTheRow(cut.FindAll(RowEventIsolation.ClickableRows)) >= 3);
    }
}
