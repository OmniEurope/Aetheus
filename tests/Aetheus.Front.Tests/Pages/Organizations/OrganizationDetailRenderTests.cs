// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Organizations;
using Aetheus.Shared.Components.Organizations;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Organizations;

public class OrganizationDetailRenderTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type PageType = typeof(OrganizationDetail);

    public OrganizationDetailRenderTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
        SetupDefaultResponses();
    }

    private void SetupDefaultResponses()
    {
        _handler.SetJsonResponse("api/organizations/1", new OrganizationDetailDto(
            Id: 1, Name: "Aetheus Org", Slug: "aetheus-org", Description: "Main org",
            CreatedAt: DateTime.UtcNow, UpdatedAt: DateTime.UtcNow,
            Members: [
                new OrganizationMemberDto(Id: 1, UserId: 2, Username: "alice", Email: "alice@test.com",
                    Role: OrganizationRole.Owner, CreatedAt: DateTime.UtcNow)
            ],
            Projects: [new OrganizationProjectDto(Id: 1, Name: "ProjectA", Status: ProjectStatus.Active)]
        ));
        _handler.SetJsonResponse("api/users", new PaginatedResult<UserDto>
        {
            Items = [new UserDto { Id = 2, Username = "alice" }],
            TotalCount = 1
        });
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>
        {
            Items = [new ProjectDto { Id = 1, Name = "ProjectA", Status = ProjectStatus.Active }],
            TotalCount = 1
        });
    }

    [Fact]
    public void Renders_WithAdminAuth()
    {
        // Members render under the "members" tab now that General is the default; seed the query.
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        nav.NavigateTo("/admin/organizations/1?tab=members");
        var cut = Render<OrganizationDetail>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));
        // The admin path loads org 1 and renders its name and member from the stub.
        var org = (OrganizationDetailDto?)PageType.GetField("_org", Priv)!.GetValue(cut.Instance);
        Assert.NotNull(org);
        Assert.Equal("Aetheus Org", org.Name);
        Assert.Contains("alice", cut.Markup);
    }

    [Fact]
    public void NonAdmin_Redirects()
    {
        // Create a non-admin context
        using var ctx = new BunitContext();
        BunitTestHelper.RegisterServices(ctx, isAdmin: false);
        var cut = ctx.Render<OrganizationDetail>(p => p.Add(x => x.Id, 1));
        // A non-admin is redirected to the app root before any organization is fetched.
        var nav = ctx.Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        Assert.EndsWith("/", nav.Uri);
    }

    [Fact]
    public async Task OnAddMember_WithNullUserId_MakesNoRequest()
    {
        var cut = Render<OrganizationDetail>(p => p.Add(x => x.Id, 1));
        await cut.InvokeAsync(() => Task.CompletedTask);
        PageType.GetField("_newUserId", Priv)!.SetValue(cut.Instance, null);
        var requestsBefore = _handler.Requests.Count;
        var method = PageType.GetMethod("OnAddMember", Priv)!;
        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [])!);
        // A null _newUserId makes OnAddMember return early - no member POST is sent.
        Assert.DoesNotContain(_handler.Requests.Skip(requestsBefore),
            r => r.Method == "POST" && r.Url.Contains("/members"));
    }

    [Fact]
    public async Task OnAddMember_WithUserId_CallsApi()
    {
        _handler.SetJsonResponse("api/organizations/1/members", new OrganizationMemberDto(
            Id: 2, UserId: 3, Username: "bob", Email: "bob@test.com",
            Role: OrganizationRole.Member, CreatedAt: DateTime.UtcNow));
        var cut = Render<OrganizationDetail>(p => p.Add(x => x.Id, 1));
        await cut.InvokeAsync(() => Task.CompletedTask);
        PageType.GetField("_newUserId", Priv)!.SetValue(cut.Instance, 3);
        // Seed _org so the member can be added to the list
        var orgField = PageType.GetField("_org", Priv)!;
        var org = (OrganizationDetailDto?)orgField.GetValue(cut.Instance);
        if (org is null)
        {
            org = new OrganizationDetailDto(1, "Test", "test", "", DateTime.UtcNow, DateTime.UtcNow,
                new List<OrganizationMemberDto>(), new List<OrganizationProjectDto>());
            orgField.SetValue(cut.Instance, org);
        }
        var method = PageType.GetMethod("OnAddMember", Priv)!;
        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [])!);

        // OnAddMember POSTs the new member then appends the returned "bob" to _org.Members
        // and clears the selection.
        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.Contains("api/organizations/1/members"));
        var orgAfter = (OrganizationDetailDto?)orgField.GetValue(cut.Instance)!;
        Assert.Contains(orgAfter!.Members, m => m.Username == "bob");
        Assert.Null((int?)PageType.GetField("_newUserId", Priv)!.GetValue(cut.Instance));
    }

    [Fact]
    public async Task OnChangeMemberRole_CallsApi()
    {
        var member = new OrganizationMemberDto(1, 2, "alice", null, OrganizationRole.Member, DateTime.UtcNow);
        _handler.SetJsonResponse("api/organizations/1/members/1", new OrganizationMemberDto(
            1, 2, "alice", null, OrganizationRole.Maintainer, DateTime.UtcNow));
        var cut = Render<OrganizationDetail>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));
        var method = PageType.GetMethod("OnChangeMemberRole", Priv)!;
        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [member, OrganizationRole.Maintainer])!);

        // OnChangeMemberRole PUTs the new role and swaps the updated member back into _org.Members.
        Assert.Contains(_handler.Requests, r => r.Method == "PUT" && r.Url.Contains("api/organizations/1/members/1"));
        var orgAfter = (OrganizationDetailDto?)PageType.GetField("_org", Priv)!.GetValue(cut.Instance);
        Assert.NotNull(orgAfter);
        Assert.Equal(OrganizationRole.Maintainer, orgAfter.Members.Single(m => m.Id == 1).Role);
    }

    [Fact]
    public async Task OnSaveProjects_CallsApi()
    {
        _handler.SetJsonResponse("api/organizations/1/projects", true);
        var cut = Render<OrganizationDetail>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));
        var method = PageType.GetMethod("OnSaveProjects", Priv)!;
        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [])!);
        // OnSaveProjects PUTs the selected project ids to the org's /projects endpoint.
        Assert.Contains(_handler.Requests, r => r.Method == "PUT" && r.Url.Contains("api/organizations/1/projects"));
    }

    [Fact]
    public void RoleOptions_ReturnThreeOptions()
    {
        var cut = Render<OrganizationDetail>(p => p.Add(x => x.Id, 1));
        var prop = PageType.GetProperty("_roleOptions", Priv)!;
        var options = (System.Collections.IList)prop.GetValue(cut.Instance)!;
        Assert.Equal(3, options.Count);
    }

    [Fact]
    public async Task LoadAsync_SetsOrg_WhenApiReturnsData()
    {
        var cut = Render<OrganizationDetail>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));
        var org = (OrganizationDetailDto?)PageType.GetField("_org", Priv)!.GetValue(cut.Instance);
        // LoadAsync populates _org from the GET, and the stubbed members/projects come along.
        Assert.NotNull(org);
        Assert.Equal("Aetheus Org", org.Name);
        Assert.Contains(org.Members, m => m.Username == "alice");
    }
}
