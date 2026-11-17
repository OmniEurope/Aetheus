// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Components.Users;
using Aetheus.Front.Tests.TestDoubles;
using Aetheus.Shared.Components.Organizations;
using AngleSharp.Dom;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Users;

/// <summary>
/// R-10 on the organizations of a user: an organization added, a role changed or a membership removed shows at once from
/// the action's answer, before the list is read again; the read runs in the background, without the page's loader, and a
/// refused action (a 409 included) leaves the rows and shows an error toast.
/// </summary>
public sealed class UserEditOrganizationRowChangesTests : BunitContext
{
    private const string ListUrl = "api/users/1/organizations";
    private readonly BunitTestHelper.TestHandler _handler;
    private readonly ImmediateDialogService _dialog;

    public UserEditOrganizationRowChangesTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
        BunitTestHelper.UseImmediateDialogs(this);
        _dialog = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        _handler.SetPaginatedJsonResponse("api/roles", new[] { new RoleDto { Id = 1, Name = "Viewer" } });
        _handler.SetJsonResponse("api/users/1", new UserDto { Id = 1, Username = "testuser", Email = "test@example.com", IsActive = true, Roles = ["Viewer"] });
        _handler.SetJsonResponse("api/users/1/effective-permissions", new UserPermissionSummaryDto());
        _handler.SetJsonResponse("api/organizations?page=1&pageSize=200", new PaginatedResult<OrganizationDto>
        {
            Items = [Available(10, "Beta"), Available(11, "Gamma")],
            TotalCount = 2,
            Page = 1,
            PageSize = 200
        });
        _handler.SetJsonResponse(HttpMethod.Get, ListUrl, new List<UserOrganizationDto> { Membership(4, 8, "Acme"), Membership(5, 9, "Zeta") });
    }

    private static OrganizationDto Available(int id, string name) => new(id, name, name.ToLowerInvariant(), "", 0, 0, default, default);

    private static UserOrganizationDto Membership(int organizationId, int memberId, string name, OrganizationRole role = OrganizationRole.Member) =>
        new(organizationId, memberId, name, name.ToLowerInvariant(), role);

    private TaskCompletionSource<List<UserOrganizationDto>> HoldTheNextListRead()
    {
        var gate = new TaskCompletionSource<List<UserOrganizationDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, ListUrl, _ => gate.Task);
        return gate;
    }

    private int ListReads() => _handler.RequestsSnapshot()
        .Count(request => request.Method == "GET" && request.Url.EndsWith(ListUrl, StringComparison.Ordinal));

    private IRenderedComponent<UserEdit> RenderOrganizations()
    {
        var cut = Render<UserEdit>(parameters => parameters.Add(page => page.Id, 1));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role='tab']")));
        cut.FindAll("[role='tab']").First(tab => tab.TextContent.Trim() == "Organizations").Click();
        cut.WaitForAssertion(() => Assert.Contains("Zeta", cut.Markup, StringComparison.Ordinal));
        return cut;
    }

    private static IRenderedComponent<OmniDropDown<int?>> OrganizationPicker(IRenderedComponent<UserEdit> cut) =>
        cut.FindComponents<OmniDropDown<int?>>().Single(dropdown => dropdown.Instance.Id == "oe-formfield-pages-users-useredit-1");

    private static IRenderedComponent<OmniDropDown<OrganizationRole>> RoleOfRow(IRenderedComponent<UserEdit> cut, int rowIndex) =>
        cut.FindComponents<OmniDropDown<OrganizationRole>>()
            .Where(dropdown => dropdown.Instance.Id != "oe-formfield-pages-users-useredit-2").ElementAt(rowIndex);

    private async Task AddAsync(IRenderedComponent<UserEdit> cut, int organizationId)
    {
        await cut.InvokeAsync(() => OrganizationPicker(cut).Instance.ValueChanged.InvokeAsync(organizationId));
        cut.WaitForAssertion(() => Assert.DoesNotContain(cut.FindAll("button"), button => button.TextContent.Contains("AddToOrganization", StringComparison.Ordinal) && button.HasAttribute("disabled")));
    }

    private static IElement AddButton(IRenderedComponent<UserEdit> cut) =>
        cut.FindAll("button").First(button => button.TextContent.Contains("AddToOrganization", StringComparison.Ordinal));

    private static IElement RemoveButton(IRenderedComponent<UserEdit> cut, string organization) =>
        cut.FindAll("tbody tr").First(row => row.TextContent.Contains(organization, StringComparison.Ordinal)).QuerySelector("button[title='Remove']")!;

    private void AssertDanger(string detail) =>
        Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniSeverity.Danger && toast.Detail == detail);

    private bool RowHolds(IRenderedComponent<UserEdit> cut, string organization) =>
        cut.FindAll("tbody tr").Any(row => row.TextContent.Contains(organization, StringComparison.Ordinal));

    // ---------- add ----------

    [Fact]
    public async Task AnAddedOrganization_ShowsTheRowAtOnce_BeforeTheReadBehindItEnds()
    {
        var cut = RenderOrganizations();
        _handler.SetJsonResponse(HttpMethod.Post, "api/organizations/10/members",
            new OrganizationMemberDto(77, 1, "testuser", null, OrganizationRole.Member, default));
        var gate = HoldTheNextListRead();
        var readsBefore = ListReads();
        await AddAsync(cut, 10);

        var action = AddButton(cut).ClickAsync(new());

        cut.WaitForAssertion(() => Assert.True(RowHolds(cut, "Beta")));
        cut.WaitForAssertion(() => Assert.Equal(readsBefore + 1, ListReads()));
        Assert.True(RowHolds(cut, "Acme"));
        Assert.DoesNotContain("Loader", cut.Markup, StringComparison.OrdinalIgnoreCase);

        gate.SetResult([Membership(4, 8, "Acme"), Membership(5, 9, "Zeta"), Membership(10, 77, "Beta"), Membership(12, 78, "Late")]);
        cut.WaitForAssertion(() => Assert.True(RowHolds(cut, "Late")));
        Assert.True(RowHolds(cut, "Beta"));
        await action;
    }

    [Fact]
    public async Task ARefusedAdd_LeavesTheRows_ShowsTheError_AndReadsNothingAgain()
    {
        var cut = RenderOrganizations();
        _handler.SetResponse(HttpMethod.Post, "api/organizations/10/members", HttpStatusCode.Conflict);
        var readsBefore = ListReads();
        await AddAsync(cut, 10);

        await AddButton(cut).ClickAsync(new());

        cut.WaitForAssertion(() => AssertDanger("SaveFailed"));
        Assert.False(RowHolds(cut, "Beta"));
        Assert.True(RowHolds(cut, "Acme"));
        Assert.Equal(readsBefore, ListReads());
    }

    // ---------- role ----------

    [Fact]
    public async Task AChangedRole_ShowsTheAnsweredRow_AndReadsTheListInTheBackground()
    {
        var cut = RenderOrganizations();
        _handler.SetJsonResponse(HttpMethod.Put, "api/organizations/4/members/8",
            new OrganizationMemberDto(8, 1, "testuser", null, OrganizationRole.Maintainer, default));
        var gate = HoldTheNextListRead();
        var readsBefore = ListReads();

        var action = cut.InvokeAsync(() => RoleOfRow(cut, 0).Instance.ValueChanged.InvokeAsync(OrganizationRole.Maintainer));

        cut.WaitForAssertion(() => Assert.Equal(readsBefore + 1, ListReads()));
        Assert.Equal(OrganizationRole.Maintainer, RoleOfRow(cut, 0).Instance.Value);
        Assert.True(RowHolds(cut, "Zeta"));

        gate.SetResult([Membership(4, 8, "Acme", OrganizationRole.Maintainer), Membership(5, 9, "Zeta", OrganizationRole.Maintainer)]);
        cut.WaitForAssertion(() => Assert.Equal(OrganizationRole.Maintainer, RoleOfRow(cut, 1).Instance.Value));
        await action;
    }

    [Fact]
    public async Task ARefusedRoleChange_KeepsTheRole_ShowsTheError_AndReadsNothingAgain()
    {
        var cut = RenderOrganizations();
        _handler.SetResponse(HttpMethod.Put, "api/organizations/4/members/8", HttpStatusCode.Conflict);
        var readsBefore = ListReads();

        await cut.InvokeAsync(() => RoleOfRow(cut, 0).Instance.ValueChanged.InvokeAsync(OrganizationRole.Maintainer));

        cut.WaitForAssertion(() => AssertDanger("SaveFailed"));
        Assert.Equal(OrganizationRole.Member, RoleOfRow(cut, 0).Instance.Value);
        Assert.Equal(readsBefore, ListReads());
    }

    // ---------- remove ----------

    [Fact]
    public async Task ARemovedMembership_LeavesTheGridAtOnce_BeforeTheReadBehindItEnds()
    {
        var cut = RenderOrganizations();
        _dialog.ConfirmResult = true;
        _handler.SetResponse(HttpMethod.Delete, "api/organizations/5/members/9", HttpStatusCode.NoContent);
        var gate = HoldTheNextListRead();
        var readsBefore = ListReads();

        var action = RemoveButton(cut, "Zeta").ClickAsync(new());

        cut.WaitForAssertion(() => Assert.False(RowHolds(cut, "Zeta")));
        cut.WaitForAssertion(() => Assert.Equal(readsBefore + 1, ListReads()));
        Assert.True(RowHolds(cut, "Acme"));

        gate.SetResult([Membership(4, 8, "Acme"), Membership(12, 78, "Late")]);
        cut.WaitForAssertion(() => Assert.True(RowHolds(cut, "Late")));
        Assert.False(RowHolds(cut, "Zeta"));
        await action;
    }

    [Fact]
    public async Task ARefusedRemoval_KeepsTheRow_ShowsTheError_AndReadsNothingAgain()
    {
        var cut = RenderOrganizations();
        _dialog.ConfirmResult = true;
        _handler.SetResponse(HttpMethod.Delete, "api/organizations/5/members/9", HttpStatusCode.Conflict);
        var readsBefore = ListReads();

        await RemoveButton(cut, "Zeta").ClickAsync(new());

        cut.WaitForAssertion(() => AssertDanger("DeleteFailed"));
        Assert.True(RowHolds(cut, "Zeta"));
        Assert.Equal(readsBefore, ListReads());
    }
}
