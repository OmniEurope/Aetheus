// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages;

public class RoleEditTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public RoleEditTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
        _handler.SetJsonResponse(
            HttpMethod.Get,
            "api/roles/0",
            new RoleDto { Id = 0, Name = string.Empty, Permissions = [] });
    }

    private static RoleDto MakeRole(int id = 1) => new()
    {
        Id = id,
        Name = "Admin",
        Description = "Administrator role",
        Permissions =
        [
            new ResourcePermissionDto { ResourceType = ResourceType.Server, Permission = Permission.Admin },
            new ResourcePermissionDto { ResourceType = ResourceType.Pipeline, Permission = Permission.Write },
            new ResourcePermissionDto { ResourceType = ResourceType.Project, Permission = Permission.Read }
        ]
    };

    private IRenderedComponent<RoleEdit> RenderRole(int id = 1)
    {
        _handler.SetJsonResponse($"api/roles/{id}", MakeRole(id));
        _handler.SetJsonResponse($"api/roles/{id}/permissions", true);
        _handler.SetJsonResponse("api/roles", new RoleDto { Id = id, Name = "Admin", Description = "Updated" });
        return Render<RoleEdit>(p => p.Add(x => x.Id, id));
    }

    [Fact]
    public void Renders_ExistingRole_ForAdmin()
    {
        var cut = RenderRole();
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar"));

        // The stubbed role name is loaded into instance state and the GET reached api/roles/1.
        var name = (string)typeof(RoleEdit).GetField("_name", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        Assert.Equal("Admin", name);
        Assert.Contains(_handler.Requests, r => r.Method == "GET" && r.Url.Contains("api/roles/1"));
    }

    [Fact]
    public void RoleIdChange_ReloadsSameComponentInstance()
    {
        _handler.SetJsonResponse("api/roles/1", MakeRole(1) with { Name = "First role" });
        _handler.SetJsonResponse("api/roles/2", MakeRole(2) with { Name = "Second role" });
        var cut = Render<RoleEdit>(p => p.Add(x => x.Id, 1));

        cut.Render(p => p.Add(x => x.Id, 2));

        cut.WaitForAssertion(() => Assert.Contains("Second role", cut.Markup));
        Assert.DoesNotContain("First role", cut.Markup);
    }

    [Fact]
    public void NewRole_RendersEmptyForm()
    {
        var cut = Render<RoleEdit>(p => p.Add(x => x.Id, 0));
        cut.WaitForState(() =>
            !(bool)typeof(RoleEdit).GetField("_loading", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!);

        // BuildPermissionRows always materializes one row per ResourceType once loading completes.
        var rows = (List<RoleEdit.PermissionRowModel>)typeof(RoleEdit)
            .GetField("_permissionRows", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        Assert.Equal(Enum.GetValues<ResourceType>().Length, rows.Count);
    }

    [Fact]
    public void NonAdmin_RedirectsToHome()
    {
        var handler = BunitTestHelper.RegisterServices(this, isAdmin: false);
        handler.SetJsonResponse("api/roles/1", new RoleDto { Id = 1, Name = "Admin", Permissions = [] });
        Render<RoleEdit>(p => p.Add(x => x.Id, 1));

        // The non-admin guard returns before fetching the role.
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("api/roles/1"));
    }

    [Fact]
    public void Renders_RoleWithPermissions()
    {
        _handler.SetJsonResponse("api/roles/2", new RoleDto
        {
            Id = 2,
            Name = "Viewer",
            Description = "Read-only role",
            Permissions =
            [
                new ResourcePermissionDto { ResourceType = ResourceType.Server, Permission = Permission.Read },
                new ResourcePermissionDto { ResourceType = ResourceType.Pipeline, Permission = Permission.Read },
                new ResourcePermissionDto { ResourceType = ResourceType.Project, Permission = Permission.Read }
            ]
        });
        var cut = Render<RoleEdit>(p => p.Add(x => x.Id, 2));
        cut.WaitForState(() =>
        {
            var r = (List<RoleEdit.PermissionRowModel>)typeof(RoleEdit)
                .GetField("_permissionRows", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
            return r.Count > 0;
        });

        // All three stubbed Read permissions map onto their resource rows as readable scope "All".
        var rows = (List<RoleEdit.PermissionRowModel>)typeof(RoleEdit)
            .GetField("_permissionRows", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        var serverRow = rows.Single(r => r.ResourceType == ResourceType.Server);
        Assert.True(serverRow.CanRead);
        Assert.False(serverRow.CanWrite);
        Assert.Equal("All", serverRow.Scope);
    }

    // --- Permission hierarchy tests ---

    [Fact]
    public void OnPermissionToggled_AdminSetsWriteAndRead()
    {
        var cut = RenderRole();
        var rows = (System.Collections.IList)typeof(RoleEdit).GetField("_permissionRows", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        var row = rows[0]!;
        row.GetType().GetProperty("CanAdmin")!.SetValue(row, true);

        var method = typeof(RoleEdit).GetMethod("OnPermissionToggled", BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(cut.Instance, [row]);

        Assert.True((bool)row.GetType().GetProperty("CanRead")!.GetValue(row)!);
        Assert.True((bool)row.GetType().GetProperty("CanWrite")!.GetValue(row)!);
        Assert.True((bool)row.GetType().GetProperty("CanAdmin")!.GetValue(row)!);
        Assert.Equal("All", (string)row.GetType().GetProperty("Scope")!.GetValue(row)!);
    }

    [Fact]
    public void OnPermissionToggled_WriteSetsRead()
    {
        var cut = RenderRole();
        var rows = (System.Collections.IList)typeof(RoleEdit).GetField("_permissionRows", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        var row = rows[0]!;
        row.GetType().GetProperty("CanAdmin")!.SetValue(row, false);
        row.GetType().GetProperty("CanWrite")!.SetValue(row, true);

        var method = typeof(RoleEdit).GetMethod("OnPermissionToggled", BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(cut.Instance, [row]);

        Assert.True((bool)row.GetType().GetProperty("CanRead")!.GetValue(row)!);
        Assert.True((bool)row.GetType().GetProperty("CanWrite")!.GetValue(row)!);
        Assert.Equal("All", (string)row.GetType().GetProperty("Scope")!.GetValue(row)!);
    }

    [Fact]
    public void OnPermissionToggled_NoneSetsScopeNone()
    {
        var cut = RenderRole();
        var rows = (System.Collections.IList)typeof(RoleEdit).GetField("_permissionRows", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        var row = rows[0]!;
        row.GetType().GetProperty("CanAdmin")!.SetValue(row, false);
        row.GetType().GetProperty("CanWrite")!.SetValue(row, false);
        row.GetType().GetProperty("CanRead")!.SetValue(row, false);

        var method = typeof(RoleEdit).GetMethod("OnPermissionToggled", BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(cut.Instance, [row]);

        Assert.Equal("None", (string)row.GetType().GetProperty("Scope")!.GetValue(row)!);
    }

    [Fact]
    public void OnScopeChanged_None_ClearsAllPermissions()
    {
        var cut = RenderRole();
        var rows = (System.Collections.IList)typeof(RoleEdit).GetField("_permissionRows", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        var row = rows[0]!;
        row.GetType().GetProperty("Scope")!.SetValue(row, "None");

        var method = typeof(RoleEdit).GetMethod("OnScopeChanged", BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(cut.Instance, [row]);

        Assert.False((bool)row.GetType().GetProperty("CanRead")!.GetValue(row)!);
        Assert.False((bool)row.GetType().GetProperty("CanWrite")!.GetValue(row)!);
        Assert.False((bool)row.GetType().GetProperty("CanAdmin")!.GetValue(row)!);
    }

    // --- OnApplyBulk ---

    [Fact]
    public async Task OnApplyBulk_NullPermission_LeavesTheStateUnchanged()
    {
        var cut = RenderRole();
        cut.WaitForState(() =>
        {
            var r = (List<RoleEdit.PermissionRowModel>)typeof(RoleEdit)
                .GetField("_permissionRows", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
            return r.Count > 0;
        });
        typeof(RoleEdit).GetField("_bulkPermission", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(cut.Instance, null);

        var rows = (List<RoleEdit.PermissionRowModel>)typeof(RoleEdit)
            .GetField("_permissionRows", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        var before = rows.Select(r => (r.Scope, r.CanRead, r.CanWrite, r.CanAdmin)).ToList();

        var method = typeof(RoleEdit).GetMethod("OnApplyBulk", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(cut.Instance, [])!;

        // Null bulk permission is a no-op - every row keeps its loaded scope/level.
        var after = rows.Select(r => (r.Scope, r.CanRead, r.CanWrite, r.CanAdmin)).ToList();
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task OnApplyBulk_WritePermission_SetsAllRowsToWriteAndPersists()
    {
        var cut = RenderRole();
        typeof(RoleEdit).GetField("_bulkPermission", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(cut.Instance, (Permission?)Permission.Write);

        var method = typeof(RoleEdit).GetMethod("OnApplyBulk", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(cut.Instance, [])!;

        var rows = (System.Collections.IList)typeof(RoleEdit).GetField("_permissionRows", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        foreach (var row in rows)
        {
            Assert.True((bool)row!.GetType().GetProperty("CanRead")!.GetValue(row)!);
            Assert.True((bool)row.GetType().GetProperty("CanWrite")!.GetValue(row)!);
            Assert.False((bool)row.GetType().GetProperty("CanAdmin")!.GetValue(row)!);
            Assert.Equal("All", (string)row.GetType().GetProperty("Scope")!.GetValue(row)!);
        }
        Assert.Contains(_handler.Requests, request =>
            request.Method == "PUT" && request.Url.Contains("api/roles/1/permissions", StringComparison.Ordinal));
    }

    // --- OnSave ---

    [Fact]
    public async Task OnSave_ExistingRole_CallsUpdate()
    {
        var cut = RenderRole();
        var method = typeof(RoleEdit).GetMethod("OnSave", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(cut.Instance, [])!;

        // The existing-role path issues a PUT to api/roles/1 (the update the test name promises).
        Assert.Contains(_handler.Requests, r => r.Method == "PUT" && r.Url.Contains("api/roles/1"));
    }

    [Fact]
    public async Task OnSavePermissions_SavesPermissions()
    {
        var cut = RenderRole();
        var method = typeof(RoleEdit).GetMethod("OnSavePermissions", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(cut.Instance, [])!;

        // Permissions are persisted via a PUT to api/roles/1/permissions.
        Assert.Contains(_handler.Requests, r => r.Method == "PUT" && r.Url.Contains("api/roles/1/permissions"));
    }

    // FRONT-023: the role holds Server 42 Read plus Project All. Editing the Project row replaces the
    // whole permission list on the server, so Server 42 Read must be in the request, unchanged.
    [Fact]
    public async Task EditingAGlobalPermission_KeepsTheRolesResourceScopedPermissions()
    {
        _handler.SetJsonResponse("api/roles/1", MakeRole(1) with
        {
            Permissions =
            [
                new ResourcePermissionDto { ResourceType = ResourceType.Server, ResourceId = 42, Permission = Permission.Read },
                new ResourcePermissionDto { ResourceType = ResourceType.Project, Permission = Permission.Admin }
            ]
        });
        _handler.SetResponse(HttpMethod.Put, "api/roles/1/permissions", System.Net.HttpStatusCode.NoContent);
        var cut = Render<RoleEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForAssertion(() => Assert.Equal("Admin",
            (string)typeof(RoleEdit).GetField("_name", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!));

        var rows = (List<RoleEdit.PermissionRowModel>)typeof(RoleEdit).GetField("_permissionRows", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        var projectRow = rows.Single(row => row.ResourceType == ResourceType.Project);
        projectRow.CanAdmin = false;
        var toggle = typeof(RoleEdit).GetMethod("OnPermissionToggledAndSaveAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(() => (Task)toggle.Invoke(cut.Instance, [projectRow])!);

        var body = _handler.RequestDetails.Last(r => r.Method == "PUT" && r.Url.Contains("api/roles/1/permissions")).Body!;
        var request = System.Text.Json.JsonSerializer.Deserialize<SetResourcePermissionsRequest>(body, JsonOptions.Web)!;
        Assert.Contains(request.Permissions, p =>
            p is { ResourceType: ResourceType.Server, ResourceId: 42, Permission: Permission.Read });
        Assert.Contains(request.Permissions, p =>
            p is { ResourceType: ResourceType.Project, ResourceId: null, Permission: Permission.Write });
        Assert.DoesNotContain(request.Permissions, p => p is { ResourceType: ResourceType.Server, ResourceId: null });
    }

    // --- OnCancel ---

    [Fact]
    public void OnCancel_NavigatesToRoles()
    {
        var cut = RenderRole();
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        var method = typeof(RoleEdit).GetMethod("OnCancel", BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(cut.Instance, []);

        Assert.EndsWith("/admin/roles", nav.Uri);
    }

    // --- Renders with all resource types ---

    [Fact]
    public void Renders_WithAllPermissionLevels()
    {
        _handler.SetJsonResponse("api/roles/3", new RoleDto
        {
            Id = 3,
            Name = "FullAccess",
            Description = "All permissions",
            Permissions = Enum.GetValues<ResourceType>().Select(rt =>
                new ResourcePermissionDto { ResourceType = rt, Permission = Permission.Admin }).ToList()
        });
        var cut = Render<RoleEdit>(p => p.Add(x => x.Id, 3));
        cut.WaitForState(() =>
        {
            var r = (List<RoleEdit.PermissionRowModel>)typeof(RoleEdit)
                .GetField("_permissionRows", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
            return r.Count > 0;
        });

        // Every resource type was stubbed at Admin level, so every row is fully granted.
        var rows = (List<RoleEdit.PermissionRowModel>)typeof(RoleEdit)
            .GetField("_permissionRows", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        Assert.All(rows, r =>
        {
            Assert.True(r.CanAdmin);
            Assert.True(r.CanWrite);
            Assert.True(r.CanRead);
        });
    }

    [Fact]
    public void AuditTab_UsesSharedEntityAuditTrail()
    {
        _handler.SetJsonResponse("api/roles/4", new RoleDto
        {
            Id = 4,
            Name = "AuditedRole",
            Description = "Role with audit logs",
            Permissions = []
        });
        // The trail's Action filter lists the audit actions.
        _handler.SetJsonResponse("api/audit/actions", new List<string> { "Created", "Updated" });
        _handler.SetJsonResponse("api/audit", new PaginatedResult<AuditLogDto>
        {
            Items =
            [
                new AuditLogDto { Id = 1, Action = "Created", EntityType = "Role", EntityId = 4, Username = "admin", Timestamp = DateTime.UtcNow },
                new AuditLogDto { Id = 2, Action = "Updated", EntityType = "Role", EntityId = 4, Username = "admin", Timestamp = DateTime.UtcNow }
            ],
            TotalCount = 2
        });

        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        nav.NavigateTo("/admin/roles/4?tab=audit");
        var cut = Render<RoleEdit>(p => p.Add(x => x.Id, 4));
        var auditTrail = cut.FindComponent<EntityAuditTrail>();

        Assert.Equal("Role", auditTrail.Instance.EntityType);
        Assert.Equal(4, auditTrail.Instance.EntityId);
        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, request =>
            request.Method == "GET"
            && request.Url.Contains("api/audit", StringComparison.Ordinal)
            && request.Url.Contains("entityType=Role", StringComparison.Ordinal)
            && request.Url.Contains("entityId=4", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task EntityAuditTrail_HeaderFilters_AreSentAsColumnFilters_BesideTheEntityScope()
    {
        // The trail's timestamp range and action list reach the audit endpoint, next to the entity
        // it is scoped to.
        _handler.SetJsonResponse("api/audit/actions", new List<string> { "Created", "Updated" });
        _handler.SetJsonResponse("api/audit", new PaginatedResult<AuditLogDto> { Items = [], TotalCount = 0 });
        var cut = Render<EntityAuditTrail>(p => p.Add(x => x.EntityType, "Role").Add(x => x.EntityId, 4));
        var grid = cut.FindComponent<AetheusDataGrid<AuditLogDto>>();

        await cut.InvokeAsync(() => grid.Instance.LoadData.InvokeAsync(new GridLoadArgs
        {
            Filters =
            [
                new GridFilterDescriptor(nameof(AuditLogDto.Timestamp), "2026-09-01T08:00:00Z",
                    OmniDataGridFilterOperator.GreaterThanOrEquals, OmniDataGridFilterOperator.LessThan, "2026-09-01T09:00:00Z"),
                new GridFilterDescriptor(nameof(AuditLogDto.Action), "Updated", OmniDataGridFilterOperator.In)
            ]
        }));

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, request =>
        {
            var url = Uri.UnescapeDataString(request.Url);
            return url.Contains("api/audit?", StringComparison.Ordinal)
                && url.Contains("entityType=Role", StringComparison.Ordinal)
                && url.Contains("entityId=4", StringComparison.Ordinal)
                && url.Contains("Filters[0].Field=Timestamp", StringComparison.Ordinal)
                && url.Contains("Filters[1].Field=Action", StringComparison.Ordinal)
                && url.Contains("Filters[1].Value=Updated", StringComparison.Ordinal);
        }));
    }

    [Fact]
    public async Task UsersTab_LoadsMembersAndAvailableUsersWithServerPagination()
    {
        _handler.SetJsonResponse("api/roles/5", MakeRole(5));
        _handler.SetJsonResponse("api/roles/5/users", new PaginatedResult<RoleUserDto>
        {
            Items = [new RoleUserDto(7, "alice", "alice@example.test", true)],
            TotalCount = 51,
            Page = 2,
            PageSize = 25
        });
        _handler.SetJsonResponse("api/roles/5/available-users", new PaginatedResult<RoleUserDto>
        {
            Items = [new RoleUserDto(8, "bob", "bob@example.test", true)],
            TotalCount = 60,
            Page = 1,
            PageSize = 25
        });
        var cut = Render<RoleEdit>(parameters => parameters.Add(component => component.Id, 5));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar"));
        var loadMembers = typeof(RoleEdit).GetMethod(
            "LoadRoleUsersAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var loadAvailable = typeof(RoleEdit).GetMethod(
            "LoadAvailableUsersAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

        await cut.InvokeAsync(async () => await (Task)loadMembers.Invoke(cut.Instance,
            [new GridLoadArgs { Skip = 25, Top = 25, OrderBy = "Username desc" }])!);
        await cut.InvokeAsync(async () => await (Task)loadAvailable.Invoke(cut.Instance,
            [new GridLoadArgs { Skip = 0, Top = 25, Filter = "bob" }])!);

        Assert.Contains(_handler.Requests, request =>
            request.Url.Contains("api/roles/5/users", StringComparison.Ordinal) &&
            request.Url.Contains("page=2", StringComparison.Ordinal) &&
            request.Url.Contains("pageSize=25", StringComparison.Ordinal) &&
            request.Url.Contains("sortDescending=true", StringComparison.Ordinal));
        Assert.Contains(_handler.Requests, request =>
            request.Url.Contains("api/roles/5/available-users", StringComparison.Ordinal) &&
            request.Url.Contains("search=bob", StringComparison.Ordinal));
        Assert.DoesNotContain(_handler.Requests, request =>
            request.Url.Contains("api/users?pageSize=200", StringComparison.Ordinal));
        Assert.Equal(51, typeof(RoleEdit).GetField(
            "_roleUsersCount", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance));
        Assert.Equal(60, typeof(RoleEdit).GetField(
            "_availableUsersCount", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance));
    }

    [Fact]
    public void NonExistentRole_RedirectsToRoles()
    {
        _handler.SetResponse("api/roles/999", System.Net.HttpStatusCode.NotFound);
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        Render<RoleEdit>(p => p.Add(x => x.Id, 999));
        Assert.EndsWith("/admin/roles", nav.Uri);
    }
}
