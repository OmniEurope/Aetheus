// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Organizations;
using Aetheus.Shared.Components.Organizations;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Organizations;

/// <summary>
/// Coverage for OrganizationDetail.razor.cs - OnAddMember, OnChangeMemberRole,
/// OnRemoveMember (dialog null = early return), OnSaveProjects.
/// </summary>
public class OrganizationDetailCoverageTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly MethodInfo ChangeMemberRole = typeof(OrganizationDetail).GetMethod("OnChangeMemberRole", Priv)!;
    private static readonly FieldInfo OrgField = typeof(OrganizationDetail).GetField("_org", Priv)!;
    private readonly BunitTestHelper.TestHandler _handler;

    public OrganizationDetailCoverageTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
    }

    private void SetupOrg(OrganizationRole bobRole = OrganizationRole.Member)
    {
        _handler.SetJsonResponse("api/organizations/1", new OrganizationDetailDto(
            Id: 1,
            Name: "Aetheus",
            Slug: "aetheus",
            Description: "",
            CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow,
            Members:
            [
                new OrganizationMemberDto(10, 2, "alice", null, OrganizationRole.Owner, DateTime.UtcNow),
                new OrganizationMemberDto(11, 3, "bob", null, bobRole, DateTime.UtcNow)
            ],
            Projects: [new OrganizationProjectDto(1, "App", ProjectStatus.Active)]
        ));
        _handler.SetJsonResponse("api/users", new PaginatedResult<UserDto>
        {
            Items = [new UserDto { Id = 2, Username = "alice" }, new UserDto { Id = 3, Username = "bob" }],
            TotalCount = 2
        });
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>
        {
            Items = [new ProjectDto { Id = 1, Name = "App" }, new ProjectDto { Id = 2, Name = "Other" }],
            TotalCount = 2
        });
    }

    // ── OnInitializedAsync ────────────────────────────────────────────────────

    [Fact]
    public void OnInit_Admin_LoadsOrganization()
    {
        SetupOrg();
        var cut = Render<OrganizationDetail>(p => p.Add(c => c.Id, 1));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        var org = typeof(OrganizationDetail).GetField("_org", Priv)!.GetValue(cut.Instance);
        Assert.NotNull(org);
    }

    // ── OnAddMember: no userId → early return ─────────────────────────────────

    [Fact]
    public async Task OnAddMember_NoUserId_ReturnsEarly()
    {
        SetupOrg();
        var cut = Render<OrganizationDetail>(p => p.Add(c => c.Id, 1));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        typeof(OrganizationDetail).GetField("_newUserId", Priv)!.SetValue(cut.Instance, (int?)null);
        var requestsBefore = _handler.Requests.Count;
        var method = typeof(OrganizationDetail).GetMethod("OnAddMember", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        // A null _newUserId returns early - no member POST is sent.
        Assert.DoesNotContain(_handler.Requests.Skip(requestsBefore),
            r => r.Method == "POST" && r.Url.Contains("/members"));
    }

    // ── OnAddMember: with userId → calls API ──────────────────────────────────

    [Fact]
    public async Task OnAddMember_WithUserId_CallsApi()
    {
        SetupOrg();
        _handler.SetJsonResponse("api/organizations/1/members", new OrganizationMemberDto(20, 5, "charlie", null, OrganizationRole.Member, DateTime.UtcNow));
        var cut = Render<OrganizationDetail>(p => p.Add(c => c.Id, 1));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        typeof(OrganizationDetail).GetField("_newUserId", Priv)!.SetValue(cut.Instance, 5);
        var method = typeof(OrganizationDetail).GetMethod("OnAddMember", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        // OnAddMember POSTs and appends the returned "charlie" to _org.Members.
        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.Contains("api/organizations/1/members"));
        var org = (OrganizationDetailDto?)typeof(OrganizationDetail).GetField("_org", Priv)!.GetValue(cut.Instance);
        Assert.NotNull(org);
        Assert.Contains(org.Members, m => m.Username == "charlie");
    }

    // ── OnChangeMemberRole ────────────────────────────────────────────────────

    [Fact]
    public async Task OnChangeMemberRole_LastOwner_IsRefusedWithoutRequest()
    {
        SetupOrg();
        var cut = Render<OrganizationDetail>(p => p.Add(c => c.Id, 1));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        var member = new OrganizationMemberDto(10, 2, "alice", null, OrganizationRole.Owner, DateTime.UtcNow);
        var method = ChangeMemberRole;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [member, OrganizationRole.Maintainer])!);

        // M6: alice is the only Owner, so nothing is sent and she stays Owner.
        Assert.DoesNotContain(_handler.Requests, r => r.Method == "PUT");
        var org = (OrganizationDetailDto?)OrgField.GetValue(cut.Instance);
        Assert.Equal(OrganizationRole.Owner, org!.Members.Single(m => m.Id == 10).Role);
    }

    [Fact]
    public async Task OnChangeMemberRole_Success_UpdatesMember()
    {
        // A second Owner exists, so alice may step down.
        SetupOrg(bobRole: OrganizationRole.Owner);
        _handler.SetJsonResponse("api/organizations/1/members/10", new OrganizationMemberDto(10, 2, "alice", null, OrganizationRole.Maintainer, DateTime.UtcNow));
        var cut = Render<OrganizationDetail>(p => p.Add(c => c.Id, 1));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        var member = new OrganizationMemberDto(10, 2, "alice", null, OrganizationRole.Owner, DateTime.UtcNow);
        var method = ChangeMemberRole;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [member, OrganizationRole.Maintainer])!);

        // The PUT returns the Maintainer member, which replaces member 10 in _org.Members.
        Assert.Contains(_handler.Requests, r => r.Method == "PUT" && r.Url.Contains("api/organizations/1/members/10"));
        var org = (OrganizationDetailDto?)OrgField.GetValue(cut.Instance);
        Assert.NotNull(org);
        Assert.Equal(OrganizationRole.Maintainer, org.Members.Single(m => m.Id == 10).Role);
    }

    // OnRemoveMember_MethodExists removed: reflexive existence assertion on a private method
    // (GetMethod on a compiled member can never be null) that verified no behaviour. The removal
    // flow gates on Dialog.Confirm, which cannot be driven from a standalone bUnit render.

    // ── OnSaveProjects ────────────────────────────────────────────────────────

    [Fact]
    public async Task OnSaveProjects_Success_Notifies()
    {
        SetupOrg();
        _handler.SetJsonResponse("api/organizations/1/projects", true);
        var cut = Render<OrganizationDetail>(p => p.Add(c => c.Id, 1));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        typeof(OrganizationDetail).GetField("_selectedProjectIds", Priv)!.SetValue(cut.Instance, new List<int> { 1 });
        var getsBefore = _handler.Requests.Count(r => r.Method == "GET" && r.Url.Contains("api/organizations/1"));
        var method = typeof(OrganizationDetail).GetMethod("OnSaveProjects", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        // A true response PUTs the selection then reloads - so the org GET fires again.
        Assert.Contains(_handler.Requests, r => r.Method == "PUT" && r.Url.Contains("api/organizations/1/projects"));
        var getsAfter = _handler.Requests.Count(r => r.Method == "GET" && r.Url.Contains("api/organizations/1"));
        Assert.True(getsAfter > getsBefore);
    }
}
