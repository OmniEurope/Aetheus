// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Components.Organizations;
using Aetheus.Front.Tests.TestDoubles;
using Aetheus.Shared.Components.Organizations;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages.Organizations;

/// <summary>Removing a member from an organization asks first; a removal the server accepts takes the member
/// off the list, a refused one is reported and keeps it.</summary>
public sealed class OrganizationMemberRemovalTests : BunitContext
{
    private const string MemberUrl = "api/organizations/1/members/7";
    private readonly BunitTestHelper.TestHandler _handler;

    public OrganizationMemberRemovalTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
        BunitTestHelper.UseImmediateDialogs(this);
        _handler.SetJsonResponse("api/organizations/1", new OrganizationDetailDto(
            Id: 1, Name: "Aetheus", Slug: "aetheus", Description: "",
            CreatedAt: DateTime.UtcNow, UpdatedAt: DateTime.UtcNow,
            Members: [new OrganizationMemberDto(7, 70, "alice", "alice@example.org", OrganizationRole.Member, DateTime.UtcNow)],
            Projects: []));
        _handler.SetJsonResponse("api/users", new PaginatedResult<UserDto> { Items = [], TotalCount = 0 });
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto> { Items = [], TotalCount = 0 });
    }

    private ImmediateDialogService Dialog => (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();

    private bool RemovalSent() => _handler.Requests.Any(request => request.Method == "DELETE" && request.Url.EndsWith(MemberUrl, StringComparison.Ordinal));

    private async Task<IRenderedComponent<OrganizationDetail>> RemoveAliceAsync()
    {
        Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>().NavigateTo("/admin/organizations/1?tab=members");
        var cut = Render<OrganizationDetail>(parameters => parameters.Add(detail => detail.Id, 1));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button[title='Remove']")));
        await cut.Find("button[title='Remove']").ClickAsync(new());
        return cut;
    }

    [Fact]
    public async Task ADeclinedRemoval_RemovesNothing()
    {
        Dialog.ConfirmResult = false;

        var cut = await RemoveAliceAsync();

        Assert.Equal("ConfirmRemoveMember", Dialog.LastConfirmMessage);
        Assert.False(RemovalSent());
        Assert.Contains("alice", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAcceptedRemoval_TakesTheMemberOffTheList()
    {
        Dialog.ConfirmResult = true;
        _handler.SetResponse(HttpMethod.Delete, MemberUrl, HttpStatusCode.NoContent);

        var cut = await RemoveAliceAsync();

        Assert.True(RemovalSent());
        Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniSeverity.Success);
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("button[title='Remove']")));
    }

    [Fact]
    public async Task ARefusedRemoval_IsReportedAndKeepsTheMember()
    {
        Dialog.ConfirmResult = true;
        _handler.SetResponse(HttpMethod.Delete, MemberUrl, HttpStatusCode.Forbidden);

        var cut = await RemoveAliceAsync();

        Assert.True(RemovalSent());
        Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniSeverity.Danger && toast.Detail == "DeleteFailed");
        Assert.NotEmpty(cut.FindAll("button[title='Remove']"));
    }
}
