// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using BackupsPage = Aetheus.Front.Components.AppBackups.Backups;

namespace Aetheus.Front.Tests.Pages.Backups;

/// <summary>R-10 on the backup policies grid: a saved policy shows what the save answered and a deleted one leaves the
/// table, both before the list is read again and without the grid's blocking bar; a refused deletion keeps the row.</summary>
public sealed class BackupsPolicyRowChangesTests : BunitContext
{
    private const string Bar = "thead > tr.omni-data-grid__progress .omni-loading-bar";
    private readonly BunitTestHelper.TestHandler _handler;

    public BackupsPolicyRowChangesTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        ((ImmediateDialogService)Services.GetRequiredService<OmniDialogService>()).ConfirmResult = true;
        _handler.SetPaginatedJsonResponse(HttpMethod.Get, "api/projects", new[] { new ProjectDto { Id = 1, Name = "aetheus" } });
        _handler.SetPaginatedJsonResponse(HttpMethod.Get, "api/servers", new[] { new ServerDto { Id = 2, Name = "vps-1" } });
    }

    private static BackupPolicyDto Policy(int id, string name) => new()
    {
        Id = id,
        Name = name,
        Enabled = true,
        ProjectId = 1,
        ServerId = 2,
        ScheduleCron = "0 3 * * *",
        RetentionCount = 14
    };

    private static PaginatedResult<BackupPolicyDto> Page(params BackupPolicyDto[] items) => new() { Items = [.. items], TotalCount = items.Length };

    private TaskCompletionSource<PaginatedResult<BackupPolicyDto>> HoldTheNextRead()
    {
        var gate = new TaskCompletionSource<PaginatedResult<BackupPolicyDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "api/backups?", _ => gate.Task);
        return gate;
    }

    private IRenderedComponent<BackupsPage> RenderWith(BackupPolicyDto policy)
    {
        _handler.SetJsonResponse(HttpMethod.Get, "api/backups?", Page(policy));
        var cut = Render<BackupsPage>();
        cut.WaitForAssertion(() => Assert.Contains(policy.Name, cut.Markup, StringComparison.Ordinal));
        return cut;
    }

    [Fact]
    public async Task ASavedPolicy_ShowsTheAnsweredRowAtOnce_WhileTheReadBehindItIsStillRunning()
    {
        var cut = RenderWith(Policy(1, "nightly-db"));
        var grid = cut.FindComponent<AetheusDataGrid<BackupPolicyDto>>().Instance;
        _handler.SetJsonResponse(HttpMethod.Put, "api/backups/1", Policy(1, "renamed-db"));
        var gate = HoldTheNextRead();

        cut.FindMenuItem("Edit")!.Click();
        cut.Find("input#oe-pages-backups-backups-1").Closest("form")!.Submit();

        cut.WaitForAssertion(() => Assert.Contains("renamed-db", cut.Markup, StringComparison.Ordinal));
        Assert.False(grid.BackgroundRefresh.IsCompleted);
        Assert.Empty(cut.FindAll(Bar));

        gate.SetResult(Page(Policy(1, "renamed-db")));
        await cut.InvokeAsync(() => grid.BackgroundRefresh);
        Assert.Contains("renamed-db", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADeletedPolicy_LeavesTheGridAtOnce_AndARefusedDeletionKeepsIt()
    {
        var cut = RenderWith(Policy(2, "weekly-files"));
        var grid = cut.FindComponent<AetheusDataGrid<BackupPolicyDto>>().Instance;

        _handler.SetResponse(HttpMethod.Delete, "api/backups/2", HttpStatusCode.Conflict);
        cut.FindMenuItem("Delete")!.Click();
        cut.WaitForAssertion(() => Assert.Contains(_handler.RequestsSnapshot(), r => r.Method == "DELETE"));
        await cut.InvokeAsync(() => grid.BackgroundRefresh);
        Assert.Contains("weekly-files", cut.Markup, StringComparison.Ordinal);

        _handler.SetResponse(HttpMethod.Delete, "api/backups/2", HttpStatusCode.NoContent);
        var gate = HoldTheNextRead();
        cut.FindMenuItem("Delete")!.Click();

        cut.WaitForAssertion(() => Assert.DoesNotContain("weekly-files", cut.Markup, StringComparison.Ordinal));
        Assert.False(grid.BackgroundRefresh.IsCompleted);
        Assert.Empty(cut.FindAll(Bar));

        gate.SetResult(Page());
        await cut.InvokeAsync(() => grid.BackgroundRefresh);
        Assert.DoesNotContain("weekly-files", cut.Markup, StringComparison.Ordinal);
    }
}
