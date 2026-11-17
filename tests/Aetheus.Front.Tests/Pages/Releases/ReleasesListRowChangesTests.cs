// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Releases;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Releases;

/// <summary>R-10 on the release list: a release whose build was started or that was promoted shows what the action
/// answered at once, before the list is read again and without the grid's blocking bar. Rollback answers a rollback
/// record, not the release, and the release sync answers a list of new releases: both keep the grid's reload.</summary>
public sealed class ReleasesListRowChangesTests : BunitContext
{
    private const string Bar = "thead > tr.omni-data-grid__progress .omni-loading-bar";
    private readonly BunitTestHelper.TestHandler _handler;
    private readonly ImmediateDialogService _dialog;

    public ReleasesListRowChangesTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        _dialog = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        _dialog.ConfirmResult = true;
        _handler.SetPaginatedJsonResponse("api/git/repos", Array.Empty<GitLightRepoDto>());
        _handler.SetJsonResponse("api/releases/filter-values", new ReleaseFilterValuesDto());
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>
        {
            Items = [new ProjectDto { Id = 1, Name = "Demo", Status = ProjectStatus.Active }],
            TotalCount = 1
        });
        _handler.SetPaginatedJsonResponse("api/pipelines", new[] { new PipelineDto { Id = 3, Name = "demo-ci", ProjectId = 1 } });
    }

    private static ReleaseDto Release(ReleaseStatus status) => new()
    {
        Id = 7,
        ProjectId = 1,
        ProjectName = "Demo",
        Version = "v7",
        BranchName = "main",
        Status = status,
        DetectedAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    private static PaginatedResult<ReleaseDto> Page(ReleaseDto release) => new() { Items = [release], TotalCount = 1 };

    private IRenderedComponent<ReleasesList> RenderList(ReleaseStatus status)
    {
        _handler.SetJsonResponse(HttpMethod.Get, "api/releases?", Page(Release(status)));
        var cut = Render<ReleasesList>();
        cut.WaitForAssertion(() => Assert.Contains("v7", cut.Markup, StringComparison.Ordinal));
        return cut;
    }

    private TaskCompletionSource<PaginatedResult<ReleaseDto>> HoldTheNextRead()
    {
        var gate = new TaskCompletionSource<PaginatedResult<ReleaseDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "api/releases?", _ => gate.Task);
        return gate;
    }

    [Fact]
    public async Task ABuildStartedFromTheList_ShowsTheAnsweredReleaseAtOnce()
    {
        var cut = RenderList(ReleaseStatus.Detected);
        var grid = cut.FindComponent<AetheusDataGrid<ReleaseDto>>().Instance;
        _dialog.OpenResult = 3;
        _handler.SetJsonResponse(HttpMethod.Post, "api/releases/7/build", Release(ReleaseStatus.Building));
        var gate = HoldTheNextRead();

        await cut.Find("button[title='TriggerBuild']").ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("button[title='TriggerBuild']")));
        Assert.False(grid.BackgroundRefresh.IsCompleted);
        Assert.Empty(cut.FindAll(Bar));

        gate.SetResult(Page(Release(ReleaseStatus.Building)));
        await cut.InvokeAsync(() => grid.BackgroundRefresh);
        Assert.Empty(cut.FindAll("button[title='TriggerBuild']"));
    }

    [Fact]
    public async Task APromotionFromTheList_ShowsTheAnsweredReleaseAtOnce()
    {
        var cut = RenderList(ReleaseStatus.Published);
        var grid = cut.FindComponent<AetheusDataGrid<ReleaseDto>>().Instance;
        _handler.SetJsonResponse(HttpMethod.Post, "api/releases/7/promote", Release(ReleaseStatus.Promoted));
        var gate = HoldTheNextRead();

        await cut.Find("button[title='Promote']").ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("button[title='Promote']")));
        Assert.False(grid.BackgroundRefresh.IsCompleted);
        Assert.Empty(cut.FindAll(Bar));

        gate.SetResult(Page(Release(ReleaseStatus.Promoted)));
        await cut.InvokeAsync(() => grid.BackgroundRefresh);
        Assert.Empty(cut.FindAll("button[title='Promote']"));
    }
}
