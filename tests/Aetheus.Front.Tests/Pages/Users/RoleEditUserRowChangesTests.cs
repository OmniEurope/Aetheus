// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages.Users;

/// <summary>R-10 on the Users tab of a role: a user taken off the role leaves the grid, and a user given the role
/// joins it, from the action's answer, while the read that confirms it is still running, with no loading bar.</summary>
public sealed class RoleEditUserRowChangesTests : BunitContext
{
    private const string Bar = "thead > tr.omni-data-grid__progress .omni-loading-bar";
    private readonly BunitTestHelper.TestHandler _handler;
    private readonly IRenderedComponent<OmniComponentsHost> _host;

    public RoleEditUserRowChangesTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
        _handler.SetOrganizations();
        _handler.SetJsonResponse(HttpMethod.Get, "api/roles/5", new RoleDto { Id = 5, Name = "Editor", Permissions = [] });
        _handler.SetJsonResponse(HttpMethod.Get, "api/roles/5/available-users", Page(User(8, "bob")));
        // The removal asks through the real overlay: the host draws the question, the test answers it.
        var overlay = Services.GetRequiredService<OmniOverlayService>();
        _host = Render<OmniComponentsHost>(parameters => parameters.Add(host => host.OverlayService, overlay));
    }

    private static RoleUserDto User(int id, string name) => new(id, name, name + "@example.test", true, 4, "Aetheus");

    private static PaginatedResult<RoleUserDto> Page(params RoleUserDto[] items) =>
        new() { Items = [.. items], TotalCount = items.Length, Page = 1, PageSize = 25 };

    private TaskCompletionSource<PaginatedResult<RoleUserDto>> HoldTheNextMembersRead()
    {
        var gate = new TaskCompletionSource<PaginatedResult<RoleUserDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "api/roles/5/users?", _ => gate.Task);
        return gate;
    }

    private IRenderedComponent<RoleEdit> RenderUsersTab(params RoleUserDto[] members)
    {
        _handler.SetJsonResponse(HttpMethod.Get, "api/roles/5/users?", Page(members));
        Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>().NavigateTo("/admin/roles/5?tab=users");
        var cut = Render<RoleEdit>(parameters => parameters.Add(component => component.Id, 5));
        cut.WaitForAssertion(() => Assert.Contains(members[0].Username, cut.Markup, StringComparison.Ordinal));
        return cut;
    }

    private static AetheusDataGrid<RoleUserDto> GridOf(IRenderedComponent<RoleEdit> cut) =>
        cut.FindComponent<AetheusDataGrid<RoleUserDto>>().Instance;

    private async Task RemoveTheSecondMemberAsync(IRenderedComponent<RoleEdit> cut)
    {
        var click = cut.FindAll("button[title='Remove']")[1].ClickAsync(new());
        await _host.WaitForElement("button.omni-confirm__action").ClickAsync(new());
        await click;
    }

    [Fact]
    public async Task ARemovedUser_LeavesTheGridAtOnce_AndARefusedRemovalKeepsIt()
    {
        var cut = RenderUsersTab(User(7, "alice"), User(9, "carol"));
        var grid = GridOf(cut);

        _handler.SetResponse(HttpMethod.Delete, "api/roles/5/users/9", HttpStatusCode.Conflict);
        await RemoveTheSecondMemberAsync(cut);
        Assert.Contains("carol", cut.Markup, StringComparison.Ordinal);

        _handler.SetResponse(HttpMethod.Delete, "api/roles/5/users/9", HttpStatusCode.NoContent);
        var gate = HoldTheNextMembersRead();
        await RemoveTheSecondMemberAsync(cut);

        cut.WaitForAssertion(() => Assert.DoesNotContain("carol", cut.Markup, StringComparison.Ordinal));
        Assert.False(grid.BackgroundRefresh.IsCompleted);
        Assert.Contains("alice", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(Bar));

        gate.SetResult(Page(User(7, "alice")));
        await cut.InvokeAsync(() => grid.BackgroundRefresh);
        Assert.DoesNotContain("carol", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("alice", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAddedUser_JoinsTheGridAtOnce_BeforeTheReadEnds()
    {
        var cut = RenderUsersTab(User(7, "alice"));
        var grid = GridOf(cut);
        var picker = cut.FindComponent<OmniAutocomplete<int?>>();
        await cut.InvokeAsync(() => picker.Instance.Search!("bo", Xunit.TestContext.Current.CancellationToken));
        await cut.InvokeAsync(() => picker.Instance.ValueChanged.InvokeAsync(8));

        _handler.SetResponse(HttpMethod.Post, "api/roles/5/users", HttpStatusCode.NoContent);
        var gate = HoldTheNextMembersRead();
        await cut.Find("button[title='AddUserToRole']").ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Contains("bob", cut.Markup, StringComparison.Ordinal));
        Assert.False(grid.BackgroundRefresh.IsCompleted);
        Assert.Contains("alice", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(Bar));

        gate.SetResult(Page(User(7, "alice"), User(8, "bob")));
        await cut.InvokeAsync(() => grid.BackgroundRefresh);
        Assert.Contains("bob", cut.Markup, StringComparison.Ordinal);
    }
}
