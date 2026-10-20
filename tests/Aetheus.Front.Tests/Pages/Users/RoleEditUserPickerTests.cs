// SPDX-License-Identifier: EUPL-1.2
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages.Users;

/// <summary>
/// The user picker of a role's Users tab searches the server: what is typed is sent as the search of the
/// role's available users, the answer becomes the picker's options, and a chosen user reads by its name.
/// </summary>
public sealed class RoleEditUserPickerTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public RoleEditUserPickerTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
        _handler.SetJsonResponse(HttpMethod.Get, "api/roles/5", new RoleDto { Id = 5, Name = "Editor", Permissions = [] });
        _handler.SetJsonResponse(HttpMethod.Get, "api/roles/5/users", new PaginatedResult<RoleUserDto>());
        _handler.SetJsonResponse(HttpMethod.Get, "api/roles/5/available-users", new PaginatedResult<RoleUserDto>
        {
            Items = [new RoleUserDto(8, "bob", "bob@example.test", true), new RoleUserDto(9, "bobby", null, true)],
            TotalCount = 2,
            Page = 1,
            PageSize = 25
        });
    }

    [Fact]
    public async Task TypingSearchesTheRolesAvailableUsers_AndOffersThemByName()
    {
        Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>().NavigateTo("/admin/roles/5?tab=users");
        var cut = Render<RoleEdit>(parameters => parameters.Add(component => component.Id, 5));
        cut.WaitForAssertion(() => Assert.Single(cut.FindComponents<OmniAutocomplete<int?>>()));
        var picker = cut.FindComponent<OmniAutocomplete<int?>>();

        var options = await cut.InvokeAsync(() => picker.Instance.Search!("bo", Xunit.TestContext.Current.CancellationToken));

        Assert.Equal([(8, "bob"), (9, "bobby")], options.Select(option => (option.Value!.Value, option.Text)));
        Assert.Contains(_handler.Requests, request =>
            request.Url.Contains("api/roles/5/available-users", StringComparison.Ordinal)
            && request.Url.Contains("search=bo", StringComparison.Ordinal)
            && request.Url.Contains("pageSize=25", StringComparison.Ordinal));
        Assert.Equal("bobby", picker.Instance.FormatValue!(9));
        Assert.Equal(string.Empty, picker.Instance.FormatValue!(42));
        Assert.False(picker.Instance.Disabled);
    }
}
