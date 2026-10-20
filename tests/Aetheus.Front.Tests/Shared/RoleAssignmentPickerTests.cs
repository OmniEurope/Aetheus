// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Bunit;

namespace Aetheus.Front.Tests.Shared;

public class RoleAssignmentPickerTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public RoleAssignmentPickerTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, authenticated: true, isAdmin: true);
    }

    private void SetupRoles(params RoleDto[] roles) =>
        _handler.SetJsonResponse("api/roles", new PaginatedResult<RoleDto>
        {
            Items = roles.ToList(),
            TotalCount = 4280,
            Page = 1,
            PageSize = 25
        });

    [Fact]
    public void InitialLoad_UsesPaginatedRolesEndpoint()
    {
        SetupRoles(new RoleDto
        {
            Id = 1,
            Name = "Admin",
            Description = "Full access",
            PermissionCount = 11,
            UserCount = 1
        });

        var cut = Render<RoleAssignmentPicker>(parameters =>
            parameters.Add(component => component.SelectedRoles, []));

        cut.WaitForAssertion(() => Assert.Contains("Admin", cut.Markup));

        Assert.Contains(_handler.Requests, request =>
            request.Url.Contains("api/roles?page=1&pageSize=25", StringComparison.Ordinal));
        Assert.DoesNotContain(_handler.Requests, request =>
            request.Url.Contains("api/users/roles", StringComparison.Ordinal));
        Assert.NotNull(cut.FindComponent<OmniTextBox>());
    }

    [Fact]
    public void Search_ReloadsFirstPageWithServerQuery()
    {
        SetupRoles(new RoleDto { Id = 2, Name = "Security Auditor" });
        var cut = Render<RoleAssignmentPicker>(parameters =>
            parameters.Add(component => component.SelectedRoles, []));
        cut.WaitForAssertion(() => Assert.Contains("Security Auditor", cut.Markup));
        _handler.Requests.Clear();

        cut.Find(".role-assignment-search").Input("security");

        // Loaded-machine ceiling: the search leaves after a 300 ms debounce and the wait is checked again
        // only on the render its answer brings; under load the request was measured sent and the check
        // passing 39 ms after bUnit's 1 s default, never a missing or wrong request (PLAN-002 B7).
        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, request =>
            request.Url.Contains("search=security", StringComparison.OrdinalIgnoreCase)));

        cut.FindAll(".role-assignment-toolbar button")
            .Single(button => button.TextContent.Contains("ClearFilters", StringComparison.Ordinal))
            .Click();

        cut.WaitForAssertion(() =>
            Assert.True(string.IsNullOrEmpty(
                cut.Find(".role-assignment-search").GetAttribute("value")),
                cut.Find(".role-assignment-search").OuterHtml));
        Assert.Contains(_handler.Requests, request =>
            request.Url.Contains("api/roles?page=1&pageSize=25", StringComparison.Ordinal)
            && !request.Url.Contains("search=", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AdvancedNameFilter_IsSentAsTheServerColumnFilter()
    {
        SetupRoles(new RoleDto { Id = 3, Name = "Security Auditor" });
        var cut = Render<RoleAssignmentPicker>(parameters =>
            parameters.Add(component => component.SelectedRoles, []));
        cut.WaitForAssertion(() => Assert.Contains("Security Auditor", cut.Markup));
        _handler.Requests.Clear();
        var load = typeof(RoleAssignmentPicker).GetMethod(
            "LoadDataAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var args = new GridLoadArgs
        {
            Skip = 0,
            Top = 25,
            Filters =
            [
                new GridFilterDescriptor(nameof(RoleDto.Name), "auditor", OmniDataGridFilterOperator.Contains)
            ]
        };

        await cut.InvokeAsync(async () => await (Task)load.Invoke(cut.Instance, [args])!);

        // The Role header filter is a real column filter (operator kept), not the free-text search
        // that also matched descriptions.
        Assert.Contains(_handler.Requests, IsNameColumnFilter);
        Assert.DoesNotContain(_handler.Requests, request =>
            request.Url.Contains("search=auditor", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SimpleNameFilter_ThroughRenderedControls_IsSentAsTheServerColumnFilter()
    {
        SetupRoles(new RoleDto { Id = 3, Name = "Security Auditor" });
        var cut = Render<RoleAssignmentPicker>(parameters =>
            parameters.Add(component => component.SelectedRoles, []));
        cut.WaitForAssertion(() => Assert.Contains("Security Auditor", cut.Markup));
        _handler.Requests.Clear();

        // The column's filter lives in its header menu, not in a second header row.
        const string nameFilter = ".role-assignment-grid th[data-omni-col='Name'] .omni-data-grid__filter-menu";
        cut.Find($"{nameFilter} input").Input("auditor");
        Assert.Empty(cut.FindAll($"{nameFilter} .omni-data-grid__filter-apply"));

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, IsNameColumnFilter));
    }

    private static bool IsNameColumnFilter((string Method, string Url) request) =>
        Uri.UnescapeDataString(request.Url).Contains("api/roles", StringComparison.Ordinal)
        && Uri.UnescapeDataString(request.Url).Contains("Filters[0].Field=Name", StringComparison.Ordinal)
        && Uri.UnescapeDataString(request.Url).Contains("Filters[0].Operator=Contains", StringComparison.Ordinal)
        && Uri.UnescapeDataString(request.Url).Contains("Filters[0].Value=auditor", StringComparison.Ordinal);

    [Fact]
    public void Toggle_PreservesSelectionOutsideTheLoadedPage()
    {
        SetupRoles(new RoleDto { Id = 2, Name = "Security Auditor" });
        List<string>? changed = null;
        var cut = Render<RoleAssignmentPicker>(parameters => parameters
            .Add(component => component.SelectedRoles, ["Existing"])
            .Add(component => component.SelectedRolesChanged,
                roles => changed = roles));
        cut.WaitForAssertion(() => Assert.Contains("Security Auditor", cut.Markup));

        cut.Find(".role-assignment-toggle input").Change(true);

        Assert.NotNull(changed);
        Assert.Contains("Existing", changed);
        Assert.Contains("Security Auditor", changed);
    }

    [Fact]
    public void AssignedOnly_ShowsOnlyTheCurrentSelection()
    {
        SetupRoles(
            new RoleDto { Id = 1, Name = "Admin" },
            new RoleDto { Id = 2, Name = "Reader" });
        var cut = Render<RoleAssignmentPicker>(parameters =>
            parameters.Add(component => component.SelectedRoles, ["Admin"]));
        cut.WaitForAssertion(() =>
            Assert.Equal(2, cut.FindAll(".role-assignment-grid tbody tr").Count));

        cut.Find(".role-assignment-filter.omni-switch").Click();

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll(".role-assignment-grid tbody tr");
            Assert.Single(rows);
            Assert.Contains("Admin", rows[0].TextContent);
            Assert.DoesNotContain("Reader", rows[0].TextContent);
        });
    }
}
