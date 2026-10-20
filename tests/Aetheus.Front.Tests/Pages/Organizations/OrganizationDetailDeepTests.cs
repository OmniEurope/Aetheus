// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Organizations;
using Aetheus.Front.Tests.TestDoubles;
using Aetheus.Shared.Components.Organizations;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Organizations;

/// <summary>
/// Deep coverage for OrganizationDetail.razor.cs - LoadAsync with rich data,
/// OnChangeMemberRole list update, OnSaveProjects with success/failure,
/// member list render, projects list render, loading state.
/// OnRemoveMember excluded (Dialog.Confirm hangs).
/// </summary>
public class OrganizationDetailDeepTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type PageType = typeof(OrganizationDetail);

    public OrganizationDetailDeepTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
    }

    private static OrganizationDetailDto MakeOrg(int id, int memberCount = 2, int projectCount = 2) =>
        new(
            Id: id,
            Name: $"Org-{id}",
            Slug: $"org-{id}",
            Description: $"Organization {id}",
            CreatedAt: DateTime.UtcNow.AddDays(-10),
            UpdatedAt: DateTime.UtcNow,
            Members: Enumerable.Range(1, memberCount).Select(m => new OrganizationMemberDto(
                Id: m,
                UserId: m + 10,
                Username: $"user{m}",
                Email: $"user{m}@test.com",
                Role: m == 1 ? OrganizationRole.Owner : OrganizationRole.Member,
                CreatedAt: DateTime.UtcNow)).ToList(),
            Projects: Enumerable.Range(1, projectCount).Select(p => new OrganizationProjectDto(
                Id: p, Name: $"Project-{p}", Status: ProjectStatus.Active)).ToList()
        );

    private void SetupForId(int id, OrganizationDetailDto? org = null)
    {
        _handler.SetJsonResponse($"api/organizations/{id}", org ?? MakeOrg(id));
        _handler.SetJsonResponse("api/users", new PaginatedResult<UserDto>
        {
            Items =
            [
                new UserDto { Id = 11, Username = "user1" },
                new UserDto { Id = 12, Username = "user2" },
                new UserDto { Id = 13, Username = "user3" }
            ],
            TotalCount = 3
        });
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>
        {
            Items =
            [
                new ProjectDto { Id = 1, Name = "Project-1", Status = ProjectStatus.Active },
                new ProjectDto { Id = 2, Name = "Project-2", Status = ProjectStatus.Active }
            ],
            TotalCount = 2
        });
    }

    // ── Test 1: LoadAsync sets org after successful API call ──────────────────

    [Fact]
    public async Task LoadAsync_SetsOrgFromApi()
    {
        SetupForId(1);
        var cut = Render<OrganizationDetail>(p => p.Add(x => x.Id, 1));

        var loadMethod = PageType.GetMethod("LoadAsync", Priv)!;
        await cut.InvokeAsync(async () => await (Task)loadMethod.Invoke(cut.Instance, [])!);

        var org = (OrganizationDetailDto?)PageType.GetField("_org", Priv)!.GetValue(cut.Instance);
        Assert.NotNull(org);
        Assert.Equal("Org-1", org.Name);
    }

    // ── Test 2: FRONT-007, the member picker searches the server ─────────────

    [Fact]
    public async Task MemberPicker_SearchesTheServer_AndOffersAUserBeyondTheFirstHundred()
    {
        SetupForId(2);
        _handler.SetJsonResponse(HttpMethod.Get, "api/users?page=1&pageSize=25&search=user101", new PaginatedResult<UserDto>
        {
            Items = [new UserDto { Id = 101, Username = "user101" }],
            TotalCount = 1
        });
        _handler.SetJsonResponse(HttpMethod.Post, "api/organizations/2/members", new OrganizationMemberDto(
            Id: 9, UserId: 101, Username: "user101", Email: null, Role: OrganizationRole.Member, CreatedAt: DateTime.UtcNow));
        Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>().NavigateTo("/admin/organizations/2?tab=members");
        var cut = Render<OrganizationDetail>(p => p.Add(x => x.Id, 2));
        cut.WaitForAssertion(() => Assert.Single(cut.FindComponents<OmniAutocomplete<int?>>()));
        Assert.DoesNotContain(_handler.Requests, request => request.Url.Contains("pageSize=100", StringComparison.Ordinal));

        var picker = cut.FindComponent<OmniAutocomplete<int?>>();
        var options = await cut.InvokeAsync(() => picker.Instance.Search!("user101", CancellationToken.None));
        Assert.Equal("user101", Assert.Single(options).Text);
        await cut.InvokeAsync(() => picker.Instance.ValueChanged.InvokeAsync(101));
        Assert.Equal("user101", picker.Instance.FormatValue!(101));
        await cut.InvokeAsync(async () => await (Task)PageType.GetMethod("OnAddMember", Priv)!.Invoke(cut.Instance, [])!);

        var post = Assert.Single(_handler.RequestDetails, request => request.Method == "POST");
        Assert.Contains("\"userId\":101", post.Body, StringComparison.Ordinal);
    }

    // ── Test 3: LoadAsync sets _allProjects list ──────────────────────────────

    [Fact]
    public async Task LoadAsync_SetsAllProjectsList()
    {
        SetupForId(3);
        var cut = Render<OrganizationDetail>(p => p.Add(x => x.Id, 3));

        var loadMethod = PageType.GetMethod("LoadAsync", Priv)!;
        await cut.InvokeAsync(async () => await (Task)loadMethod.Invoke(cut.Instance, [])!);

        var projects = (List<ProjectDto>)PageType.GetField("_allProjects", Priv)!.GetValue(cut.Instance)!;
        Assert.Equal(2, projects.Count);
    }

    // ── Test 4: Members show in markup ───────────────────────────────────────

    [Fact]
    public async Task Render_ShowsMembersInMarkup()
    {
        SetupForId(4);
        // Members now live under the "members" tab (General is the default tab); seed the query so
        // the members tab is active and its markup renders.
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        nav.NavigateTo("/admin/organizations/4?tab=members");
        var cut = Render<OrganizationDetail>(p => p.Add(x => x.Id, 4));

        var loadMethod = PageType.GetMethod("LoadAsync", Priv)!;
        await cut.InvokeAsync(async () => await (Task)loadMethod.Invoke(cut.Instance, [])!);
        cut.Render();

        Assert.Contains("user1", cut.Markup);
    }

    // ── Test 5: OnChangeMemberRole updates member in list ────────────────────

    [Fact]
    public async Task OnChangeMemberRole_SuccessResponse_UpdatesMemberInList()
    {
        SetupForId(5);
        var org = MakeOrg(5);
        _handler.SetJsonResponse($"api/organizations/5", org);

        var updatedMember = new OrganizationMemberDto(
            Id: 1, UserId: 11, Username: "user1", Email: "user1@test.com",
            Role: OrganizationRole.Maintainer, CreatedAt: DateTime.UtcNow);
        _handler.SetJsonResponse("api/organizations/5/members/1", updatedMember);

        var cut = Render<OrganizationDetail>(p => p.Add(x => x.Id, 5));

        var loadMethod = PageType.GetMethod("LoadAsync", Priv)!;
        await cut.InvokeAsync(async () => await (Task)loadMethod.Invoke(cut.Instance, [])!);

        var member = new OrganizationMemberDto(
            Id: 1, UserId: 11, Username: "user1", Email: "user1@test.com",
            Role: OrganizationRole.Member, CreatedAt: DateTime.UtcNow);

        var method = PageType.GetMethod("OnChangeMemberRole", Priv)!;
        await cut.InvokeAsync(async () =>
            await (Task)method.Invoke(cut.Instance, [member, OrganizationRole.Maintainer])!);

        // The PUT returns the Maintainer member, which is swapped into _org.Members in place.
        Assert.Contains(_handler.Requests, r => r.Method == "PUT" && r.Url.Contains("api/organizations/5/members/1"));
        var orgState = (OrganizationDetailDto?)PageType.GetField("_org", Priv)!.GetValue(cut.Instance);
        Assert.NotNull(orgState);
        Assert.Equal(OrganizationRole.Maintainer, orgState.Members.Single(m => m.Id == 1).Role);
    }

    // ── Test 6: OnChangeMemberRole null API response does not crash ───────────

    [Fact]
    public async Task OnChangeMemberRole_NullResponse_NoException()
    {
        SetupForId(6);
        _handler.SetResponse("api/organizations/6/members/1", System.Net.HttpStatusCode.BadRequest);

        var cut = Render<OrganizationDetail>(p => p.Add(x => x.Id, 6));

        var loadMethod = PageType.GetMethod("LoadAsync", Priv)!;
        await cut.InvokeAsync(async () => await (Task)loadMethod.Invoke(cut.Instance, [])!);

        var member = new OrganizationMemberDto(
            Id: 1, UserId: 11, Username: "user1", Email: null,
            Role: OrganizationRole.Member, CreatedAt: DateTime.UtcNow);

        var method = PageType.GetMethod("OnChangeMemberRole", Priv)!;
        await cut.InvokeAsync(async () =>
            await (Task)method.Invoke(cut.Instance, [member, OrganizationRole.Maintainer])!);

        // The 400 makes the update return null, so the member list is left unchanged -
        // member 1 keeps its original Owner role from MakeOrg.
        var org = (OrganizationDetailDto?)PageType.GetField("_org", Priv)!.GetValue(cut.Instance);
        Assert.NotNull(org);
        Assert.Equal(OrganizationRole.Owner, org.Members.Single(m => m.Id == 1).Role);
    }

    // ── Test 7: OnSaveProjects success reloads ────────────────────────────────

    [Fact]
    public async Task OnSaveProjects_Success_ReloadsData()
    {
        SetupForId(7);
        _handler.SetJsonResponse("api/organizations/7/projects", true);

        var cut = Render<OrganizationDetail>(p => p.Add(x => x.Id, 7));

        var loadMethod = PageType.GetMethod("LoadAsync", Priv)!;
        await cut.InvokeAsync(async () => await (Task)loadMethod.Invoke(cut.Instance, [])!);

        var method = PageType.GetMethod("OnSaveProjects", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        // A true response triggers a PUT then a full LoadAsync reload - so the org GET
        // is hit at least twice (initial load + post-save reload).
        Assert.Contains(_handler.Requests, r => r.Method == "PUT" && r.Url.Contains("api/organizations/7/projects"));
        Assert.True(_handler.Requests.Count(r => r.Method == "GET" && r.Url.Contains("api/organizations/7")) >= 2);
    }

    // ── Test 8: OnSaveProjects false response does not crash ──────────────────

    [Fact]
    public async Task OnSaveProjects_False_NoException()
    {
        SetupForId(8);
        _handler.SetJsonResponse("api/organizations/8/projects", false);

        var cut = Render<OrganizationDetail>(p => p.Add(x => x.Id, 8));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        var method = PageType.GetMethod("OnSaveProjects", Priv)!;
        var ex = await Record.ExceptionAsync(async () =>
            await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!));

        // OnSaveProjects sends the assign PUT and completes without throwing on any response.
        Assert.Null(ex);
        Assert.Contains(_handler.Requests, r => r.Method == "PUT" && r.Url.Contains("api/organizations/8/projects"));
    }

    [Fact]
    public async Task Front006_ARefusedAssignment_RestoresTheConfirmedProjects_NotTheUnsavedSelection()
    {
        SetupForId(8);
        _handler.SetResponse(HttpMethod.Put, "api/organizations/8/projects", System.Net.HttpStatusCode.BadRequest);
        var cut = Render<OrganizationDetail>(p => p.Add(x => x.Id, 8));
        cut.WaitForAssertion(() => Assert.NotNull(PageType.GetField("_org", Priv)!.GetValue(cut.Instance)));

        // What @bind-Value does before its :after handler runs: the field already holds the new selection.
        IReadOnlyList<int> unsaved = [1];
        PageType.GetField("_selectedProjectIds", Priv)!.SetValue(cut.Instance, unsaved);
        await cut.InvokeAsync(async () =>
            await (Task)PageType.GetMethod("OnProjectsChangedAsync", Priv)!.Invoke(cut.Instance, [unsaved])!);

        var selected = (IReadOnlyList<int>)PageType.GetField("_selectedProjectIds", Priv)!.GetValue(cut.Instance)!;
        Assert.Equal([1, 2], selected.Order());
    }

    // ── Test 9: OnAddMember with valid userId calls API ───────────────────────

    [Fact]
    public async Task OnAddMember_ValidUserId_AddsMemberToOrg()
    {
        SetupForId(9);
        var newMember = new OrganizationMemberDto(
            Id: 99, UserId: 13, Username: "user3", Email: "user3@test.com",
            Role: OrganizationRole.Member, CreatedAt: DateTime.UtcNow);
        _handler.SetJsonResponse("api/organizations/9/members", newMember);

        var cut = Render<OrganizationDetail>(p => p.Add(x => x.Id, 9));

        var loadMethod = PageType.GetMethod("LoadAsync", Priv)!;
        await cut.InvokeAsync(async () => await (Task)loadMethod.Invoke(cut.Instance, [])!);

        // _org is loaded by LoadAsync above, so it always has a Members list here.
        var org = (OrganizationDetailDto?)PageType.GetField("_org", Priv)!.GetValue(cut.Instance);
        Assert.NotNull(org);
        Assert.DoesNotContain(org.Members, m => m.Username == "user3");

        PageType.GetField("_newUserId", Priv)!.SetValue(cut.Instance, 13);

        var method = PageType.GetMethod("OnAddMember", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        // OnAddMember POSTs and appends the returned "user3" member to _org.Members.
        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.Contains("api/organizations/9/members"));
        var orgAfter = (OrganizationDetailDto?)PageType.GetField("_org", Priv)!.GetValue(cut.Instance)!;
        Assert.Contains(orgAfter!.Members, m => m.Username == "user3");
    }

    // ── Test 10: _loading becomes false after LoadAsync ───────────────────────

    [Fact]
    public async Task LoadAsync_SetsLoadingFalse()
    {
        SetupForId(10);
        var cut = Render<OrganizationDetail>(p => p.Add(x => x.Id, 10));

        var loadMethod = PageType.GetMethod("LoadAsync", Priv)!;
        await cut.InvokeAsync(async () => await (Task)loadMethod.Invoke(cut.Instance, [])!);

        var loading = (bool)PageType.GetField("_loading", Priv)!.GetValue(cut.Instance)!;
        Assert.False(loading);
    }

    [Fact]
    public void MemberRowControls_DoNotReachTheRow()
    {
        // SITE-FUNC-HOME-CAPABILITY-001: a link, button or form control in a clickable row stops click and
        // Enter/Space, or the row click runs after its own and wins the navigation.
        SetupForId(1);

        var cut = Render<OrganizationDetail>(p => p.Add(x => x.Id, 1));
        cut.WaitForAssertion(() => Assert.Contains("Org-1", cut.Markup));
        cut.FindAll("[role='tab']").First(tab => tab.TextContent.Contains("Members", StringComparison.Ordinal)).Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(RowEventIsolation.ClickableRows)));

        // Per member: the user link, the role list and the remove button.
        Assert.True(RowEventIsolation.AssertNoRowControlReachesTheRow(cut.FindAll(RowEventIsolation.ClickableRows)) >= 3);
    }
}
