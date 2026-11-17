// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OrganizationsPage = Aetheus.Front.Components.Organizations.Organizations;
using RolesPage = Aetheus.Front.Components.Users.Roles;

namespace Aetheus.Front.Tests.Pages.Users;

/// <summary>R-10 on the Roles and Organizations grids: a deleted row leaves the table at once, before the list is read
/// again; a refused deletion leaves it where it was.</summary>
public sealed class RolesAndOrganizationsDeleteRowTests : BunitContext
{
    private const string Bar = "thead > tr.omni-data-grid__progress .omni-loading-bar";
    private readonly BunitTestHelper.TestHandler _handler;
    private readonly ImmediateDialogService _dialog;

    public RolesAndOrganizationsDeleteRowTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
        BunitTestHelper.UseImmediateDialogs(this);
        _dialog = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        _dialog.ConfirmResult = true;
    }

    private static OrganizationDto Org(int id, string name) =>
        new(id, name, name.ToLowerInvariant(), string.Empty, 0, 0, new DateTime(2026, 10, 1), new DateTime(2026, 10, 1));

    private static PaginatedResult<T> Page<T>(params T[] items) => new() { Items = [.. items], TotalCount = items.Length };

    private TaskCompletionSource<PaginatedResult<T>> HoldTheNextRead<T>(string urlContains)
    {
        var gate = new TaskCompletionSource<PaginatedResult<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, urlContains, _ => gate.Task);
        return gate;
    }

    [Fact]
    public async Task ADeletedRole_LeavesTheGridAtOnce_AndARefusedDeletionKeepsIt()
    {
        var viewer = new RoleDto { Id = 2, Name = "Viewer", UserCount = 5 };
        _handler.SetJsonResponse("api/roles", Page(new RoleDto { Id = 1, Name = "Admin", UserCount = 2 }, viewer));
        var cut = Render<RolesPage>();
        cut.WaitForAssertion(() => Assert.Contains("Viewer", cut.Markup, StringComparison.Ordinal));
        var grid = cut.FindComponent<AetheusDataGrid<RoleDto>>().Instance;

        _handler.SetResponse(HttpMethod.Delete, "api/roles/2", HttpStatusCode.Conflict);
        await cut.FindAll("button[title='Delete']")[0].ClickAsync(new());
        Assert.Contains("Viewer", cut.Markup, StringComparison.Ordinal);

        _handler.SetResponse(HttpMethod.Delete, "api/roles/2", HttpStatusCode.NoContent);
        var gate = HoldTheNextRead<RoleDto>("api/roles?");
        await cut.FindAll("button[title='Delete']")[0].ClickAsync(new());

        cut.WaitForAssertion(() => Assert.DoesNotContain("Viewer", cut.Markup, StringComparison.Ordinal));
        Assert.False(grid.BackgroundRefresh.IsCompleted);
        Assert.Contains("Admin", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(Bar));

        gate.SetResult(Page(new RoleDto { Id = 1, Name = "Admin", UserCount = 2 }));
        await cut.InvokeAsync(() => grid.BackgroundRefresh);
        Assert.DoesNotContain("Viewer", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADeletedOrganization_LeavesTheGridAtOnce_AndARefusedDeletionKeepsIt()
    {
        _handler.SetJsonResponse("api/organizations", Page(Org(1, "Alpha"), Org(2, "Beta")));
        var cut = Render<OrganizationsPage>();
        cut.WaitForAssertion(() => Assert.Contains("Beta", cut.Markup, StringComparison.Ordinal));
        var grid = cut.FindComponent<AetheusDataGrid<OrganizationDto>>().Instance;

        _handler.SetResponse(HttpMethod.Delete, "api/organizations/2", HttpStatusCode.Conflict);
        await cut.FindAll("button[title='Delete']")[1].ClickAsync(new());
        Assert.Contains("Beta", cut.Markup, StringComparison.Ordinal);

        _handler.SetResponse(HttpMethod.Delete, "api/organizations/2", HttpStatusCode.NoContent);
        var gate = HoldTheNextRead<OrganizationDto>("api/organizations?");
        await cut.FindAll("button[title='Delete']")[1].ClickAsync(new());

        cut.WaitForAssertion(() => Assert.DoesNotContain("Beta", cut.Markup, StringComparison.Ordinal));
        Assert.False(grid.BackgroundRefresh.IsCompleted);
        Assert.Contains("Alpha", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(Bar));

        gate.SetResult(Page(Org(1, "Alpha")));
        await cut.InvokeAsync(() => grid.BackgroundRefresh);
        Assert.DoesNotContain("Beta", cut.Markup, StringComparison.Ordinal);
    }
}
