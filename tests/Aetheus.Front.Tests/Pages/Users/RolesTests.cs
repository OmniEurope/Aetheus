// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Tests.TestDoubles;
using Bunit;

namespace Aetheus.Front.Tests.Pages;

public class RolesTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public RolesTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
    }

    [Fact]
    public void InitialRender_LoadsAndRendersPaginatedRoles()
    {
        _handler.SetJsonResponse("api/roles", new PaginatedResult<RoleDto>
        {
            Items =
            [
                new() { Id = 1, Name = "Admin", UserCount = 2 },
                new() { Id = 2, Name = "Viewer", UserCount = 5 }
            ],
            TotalCount = 2
        });

        var cut = Render<Roles>();

        cut.WaitForAssertion(() => Assert.Multiple(() =>
        {
            Assert.Contains("Admin", cut.Markup);
            Assert.Contains("Viewer", cut.Markup);
            Assert.DoesNotContain("rz-data-grid-loading", cut.Markup);
            Assert.Contains(_handler.Requests, request => request.Url.Contains("api/roles", StringComparison.Ordinal));
        }));
    }

    [Fact]
    public void Renders_RolesPage_Empty()
    {
        _handler.SetJsonResponse("api/roles", new PaginatedResult<RoleDto>());

        var cut = Render<Roles>();
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Roles", cut.Markup);
            Assert.DoesNotContain("rz-data-grid-loading", cut.Markup);
        });
    }

    [Fact]
    public void NonAdmin_RedirectsToHome()
    {
        var handler = BunitTestHelper.RegisterServices(this, isAdmin: false);
        handler.SetJsonResponse("api/roles", new PaginatedResult<RoleDto>());

        var cut = Render<Roles>();
        // The admin guard redirects before LoadAsync, so the roles list is never fetched.
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("api/roles"));
    }

    [Fact]
    public void RowControls_DoNotReachTheRow()
    {
        // SITE-FUNC-HOME-CAPABILITY-001: a link, button or form control in a clickable row stops click and
        // Enter/Space, or the row click runs after its own and wins the navigation.
        _handler.SetJsonResponse("api/roles", new PaginatedResult<RoleDto>
        {
            Items = [new() { Id = 2, Name = "Viewer", UserCount = 5 }],
            TotalCount = 1
        });

        var cut = Render<Roles>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(RowEventIsolation.ClickableRows)), TimeSpan.FromSeconds(3));

        // The role link, the clone and the delete buttons.
        Assert.True(RowEventIsolation.AssertNoRowControlReachesTheRow(cut.FindAll(RowEventIsolation.ClickableRows)) >= 3);
    }
}
