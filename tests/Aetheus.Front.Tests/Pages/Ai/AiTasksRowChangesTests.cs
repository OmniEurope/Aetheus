// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Components.AiTasks;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Ai;

/// <summary>R-10 on the AI tasks grid: a deleted task leaves the table before the list is read again and without the
/// grid's blocking bar, a refused deletion (a 409 included) keeps the row and says so. A task saved in the dialog is read
/// again behind the rows, quietly: the dialog answers no row, and the grid shows no bar while it waits.</summary>
public sealed class AiTasksRowChangesTests : BunitContext
{
    private const string Bar = "thead > tr.omni-data-grid__progress .omni-loading-bar";
    private const string ListUrl = "api/ai/tasks?";
    private readonly BunitTestHelper.TestHandler _handler;
    private readonly ImmediateDialogService _dialogs;

    public AiTasksRowChangesTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
        BunitTestHelper.UseImmediateDialogs(this);
        _dialogs = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        _dialogs.ConfirmResult = true;
        _handler.SetJsonResponse("api/ai/consumption", new AiConsumptionDto());
        _handler.SetJsonResponse("api/ai/profile-options", new List<AiRunnerProfileDto>());
        _handler.SetJsonResponse(HttpMethod.Get, ListUrl, Page(Definition(7, "nightly review"), Definition(8, "weekly summary")));
    }

    private static AiTaskDefinitionDto Definition(int id, string name) => new() { Id = id, Name = name, ServerId = 20, ServerName = "runner" };

    private static PaginatedResult<AiTaskDefinitionDto> Page(params AiTaskDefinitionDto[] items) => new() { Items = [.. items], TotalCount = items.Length };

    private TaskCompletionSource<PaginatedResult<AiTaskDefinitionDto>> HoldTheNextListRead()
    {
        var gate = new TaskCompletionSource<PaginatedResult<AiTaskDefinitionDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, ListUrl, _ => gate.Task);
        return gate;
    }

    private IRenderedComponent<AiTasks> RenderList()
    {
        var cut = Render<AiTasks>();
        cut.WaitForAssertion(() => Assert.Contains("weekly summary", cut.Markup, StringComparison.Ordinal));
        return cut;
    }

    [Fact]
    public async Task ADeletedTask_LeavesTheGridAtOnce_AndARefusedDeletionKeepsIt()
    {
        var cut = RenderList();
        var grid = cut.FindComponent<AetheusDataGrid<AiTaskDefinitionDto>>().Instance;

        _handler.SetResponse(HttpMethod.Delete, "api/ai/tasks/7", HttpStatusCode.Conflict);
        await cut.FindMenuItem("Delete")!.ClickAsync(new());
        await cut.InvokeAsync(() => grid.BackgroundRefresh);
        Assert.Contains("nightly review", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniSeverity.Danger && toast.Detail == "DeleteFailed");

        _handler.SetResponse(HttpMethod.Delete, "api/ai/tasks/7", HttpStatusCode.NoContent);
        var gate = HoldTheNextListRead();
        await cut.FindMenuItem("Delete")!.ClickAsync(new());

        cut.WaitForAssertion(() => Assert.DoesNotContain("nightly review", cut.Markup, StringComparison.Ordinal));
        Assert.False(grid.BackgroundRefresh.IsCompleted);
        Assert.Contains("weekly summary", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(Bar));

        gate.SetResult(Page(Definition(8, "weekly summary")));
        await cut.InvokeAsync(() => grid.BackgroundRefresh);
        Assert.DoesNotContain("nightly review", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASavedTask_IsReadAgainBehindTheRows_WithoutTheBar()
    {
        var cut = RenderList();
        var grid = cut.FindComponent<AetheusDataGrid<AiTaskDefinitionDto>>().Instance;
        _dialogs.OpenResult = true;
        var gate = HoldTheNextListRead();

        var edit = cut.FindMenuItem("Edit")!.ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Equal(2, _handler.RequestsSnapshot().Count(r => r.Method == "GET" && r.Url.Contains(ListUrl, StringComparison.Ordinal))));
        Assert.False(edit.IsCompleted);
        Assert.Contains("nightly review", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(Bar));

        gate.SetResult(Page(Definition(7, "nightly review renamed"), Definition(8, "weekly summary")));
        await edit;
        await cut.InvokeAsync(() => grid.BackgroundRefresh);
        Assert.Contains("nightly review renamed", cut.Markup, StringComparison.Ordinal);
    }
}
