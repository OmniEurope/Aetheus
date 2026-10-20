// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Users;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Users;

/// <summary> One search for Users, Organizations and Roles. Each section shows its count in
/// brackets only when positive, and the search moves to the only section holding a result.
/// </summary>
public sealed class AdminIdentitySearchTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public AdminIdentitySearchTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    private void Counts(int users, int organizations, int roles)
    {
        _handler.SetJsonResponse("api/users?", new PaginatedResult<UserDto> { TotalCount = users, Page = 1, PageSize = 1 });
        _handler.SetJsonResponse("api/organizations?", new PaginatedResult<OrganizationDto> { TotalCount = organizations, Page = 1, PageSize = 1 });
        _handler.SetJsonResponse("api/roles?", new PaginatedResult<RoleDto> { TotalCount = roles, Page = 1, PageSize = 1 });
    }

    [Fact]
    public async Task Search_MovesToTheOnlySectionWithResults()
    {
        Counts(users: 0, organizations: 2, roles: 0);
        var search = Services.GetRequiredService<AdminIdentitySearch>();

        var section = await search.SetAsync("ops", AdminIdentitySearch.UsersSection);

        Assert.Equal(AdminIdentitySearch.OrganizationsSection, section);
        Assert.Contains(_handler.Requests, request => request.Url.Contains("api/users?", StringComparison.Ordinal)
            && request.Url.Contains("search=ops", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1, 2, 0)] // the active section has a result
    [InlineData(0, 2, 3)] // several other sections have one
    [InlineData(0, 0, 0)] // nothing anywhere
    public async Task Search_StaysOnTheActiveSection_Otherwise(int users, int organizations, int roles)
    {
        Counts(users, organizations, roles);
        var search = Services.GetRequiredService<AdminIdentitySearch>();

        Assert.Equal(AdminIdentitySearch.UsersSection, await search.SetAsync("ops", AdminIdentitySearch.UsersSection));
    }

    [Fact]
    public async Task Nav_ShowsACountInBracketsOnlyWhenPositive_AndTheSearchOnTheRight()
    {
        Counts(users: 0, organizations: 2, roles: 1);
        var search = Services.GetRequiredService<AdminIdentitySearch>();
        await search.SetAsync("ops", AdminIdentitySearch.OrganizationsSection);

        var nav = Render<AdminIdentityNav>(parameters => parameters.Add(component => component.ActiveSection, AdminIdentitySearch.OrganizationsSection));

        var links = nav.FindAll(".iam-section-link").Select(link => link.TextContent.Trim()).ToList();
        Assert.Equal(["Users", "Organizations (2)", "Roles (1)"], links);
        Assert.NotEmpty(nav.FindAll(".iam-section-bar .iam-section-search"));
    }

    [Fact]
    public async Task ClearingTheSearch_DropsTheCounts()
    {
        Counts(users: 4, organizations: 2, roles: 1);
        var search = Services.GetRequiredService<AdminIdentitySearch>();
        await search.SetAsync("ops", AdminIdentitySearch.UsersSection);

        await search.SetAsync(string.Empty, AdminIdentitySearch.UsersSection);

        Assert.Null(search.Search);
        Assert.Null(search.CountFor(AdminIdentitySearch.UsersSection));
    }
}
