// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Tests.TestDoubles;
using Aetheus.Shared.Components.Organizations;
using Bunit;
using OrganizationsPage = Aetheus.Front.Components.Organizations.Organizations;

namespace Aetheus.Front.Tests.Pages;

public class OrganizationsListTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public OrganizationsListTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
    }

    [Fact]
    public void Renders_WithData()
    {
        var data = new PaginatedResult<OrganizationDto>
        {
            Items =
            [
                new OrganizationDto(1, "Acme", "acme", "Acme Corp", 5, 3, DateTime.UtcNow, DateTime.UtcNow),
                new OrganizationDto(2, "Beta", "beta", "Beta Inc", 2, 1, DateTime.UtcNow, DateTime.UtcNow)
            ],
            TotalCount = 2
        };
        _handler.SetJsonResponse("api/organizations", data);

        var cut = Render<OrganizationsPage>();
        // After load, markup should contain org names
        cut.WaitForState(() => cut.Markup.Contains("Acme"));
        Assert.Contains("Beta", cut.Markup);
    }

    [Fact]
    public void Renders_EmptyList()
    {
        var data = new PaginatedResult<OrganizationDto> { Items = [], TotalCount = 0 };
        _handler.SetJsonResponse("api/organizations", data);

        var cut = Render<OrganizationsPage>();
        // FRONTTEST-004: the grid says it is empty only once the load has settled.
        cut.WaitForAssertion(() => Assert.Contains("NoRecords", cut.Markup));
        Assert.Contains("Organizations", cut.Markup);
    }

    [Fact]
    public async Task LoadData_RequestsRequestedServerPageAndSort()
    {
        _handler.SetJsonResponse("api/organizations", new PaginatedResult<OrganizationDto>
        {
            Items = [],
            TotalCount = 250,
            Page = 2,
            PageSize = 25
        });
        var cut = Render<OrganizationsPage>();
        var method = typeof(OrganizationsPage).GetMethod("LoadDataAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance,
            [new GridLoadArgs { Skip = 25, Top = 25, OrderBy = "Slug desc" }])!);

        Assert.Contains(_handler.Requests, request =>
            request.Url.Contains("page=2", StringComparison.Ordinal)
            && request.Url.Contains("pageSize=25", StringComparison.Ordinal)
            && request.Url.Contains("sortBy=Slug", StringComparison.Ordinal)
            && request.Url.Contains("sortDescending=true", StringComparison.Ordinal));
    }

    [Fact]
    public void RowControls_DoNotReachTheRow()
    {
        // SITE-FUNC-HOME-CAPABILITY-001: a link, button or form control in a clickable row stops click and
        // Enter/Space, or the row click runs after its own and wins the navigation.
        _handler.SetJsonResponse("api/organizations", new PaginatedResult<OrganizationDto>
        {
            Items = [new OrganizationDto(1, "Acme", "acme", "Acme Corp", 5, 3, DateTime.UtcNow, DateTime.UtcNow)],
            TotalCount = 1
        });

        var cut = Render<OrganizationsPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(RowEventIsolation.ClickableRows)));

        Assert.True(RowEventIsolation.AssertNoRowControlReachesTheRow(cut.FindAll(RowEventIsolation.ClickableRows)) >= 2);
    }
}
